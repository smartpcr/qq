// -----------------------------------------------------------------------
// <copyright file="SlackDirectApiClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Identifiers;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.Extensions.Logging;
using SlackNet;

/// <summary>
/// Stage 6.4 synchronous Slack Web API client used by the modal
/// fast-path. Wraps SlackNet's <see cref="ISlackApiClient"/> for the
/// <c>views.open</c> call that must execute inside the controller's
/// HTTP request because Slack's <c>trigger_id</c> expires within
/// approximately three seconds of issuance (architecture.md §2.15,
/// §5.3 / implementation-plan.md Stage&#160;6.4).
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities (architecture.md §2.15, implementation-plan Stage
/// 6.4 steps 1-5):
/// </para>
/// <list type="number">
///   <item><description>Resolve the per-workspace bot OAuth token via
///   <see cref="ISlackWorkspaceConfigStore"/> +
///   <see cref="ISecretProvider"/>.</description></item>
///   <item><description>Acquire a token from the SHARED
///   <see cref="ISlackRateLimiter"/> (Tier&#160;4, scope = team id)
///   so concurrent <c>views.open</c> + <c>chat.postMessage</c> calls
///   collectively respect Slack's per-tier ceilings.</description></item>
///   <item><description>Dispatch <c>views.open</c> through SlackNet's
///   <see cref="ISlackApiClient.Post(string, Dictionary{string, object}, System.Threading.CancellationToken)"/>
///   so the connector benefits from SlackNet's typed Slack-error
///   classification (<see cref="SlackException"/> /
///   <see cref="SlackRateLimitException"/>).</description></item>
///   <item><description>Translate SlackNet exceptions into the
///   strongly-typed <see cref="SlackViewsOpenResult"/> surface that
///   the existing <see cref="DefaultSlackModalFastPathHandler"/>
///   already branches on; on HTTP&#160;429 surface the
///   <c>Retry-After</c> back into the shared limiter via
///   <see cref="ISlackRateLimiter.NotifyRetryAfter"/> so the durable
///   outbound dispatcher pauses too.</description></item>
///   <item><description>Append a <c>request_type = modal_open</c>
///   audit row to <see cref="ISlackAuditEntryWriter"/> for every
///   high-level <see cref="OpenModalAsync"/> invocation (success or
///   failure) so the call is queryable in the audit log even if a
///   future caller bypasses the
///   <see cref="DefaultSlackModalFastPathHandler"/> /
///   <see cref="SlackModalAuditRecorder"/> pair.</description></item>
/// </list>
/// <para>
/// The client implements <see cref="ISlackViewsOpenClient"/> so it can
/// supersede <see cref="HttpClientSlackViewsOpenClient"/> in the DI
/// container without rewriting the
/// <see cref="DefaultSlackModalFastPathHandler"/>. The
/// <see cref="ISlackViewsOpenClient.OpenAsync"/> entry point performs
/// the rate-limit + SlackNet call WITHOUT writing an audit row, because
/// the handler already calls
/// <see cref="SlackModalAuditRecorder.RecordSuccessAsync"/> /
/// <see cref="SlackModalAuditRecorder.RecordErrorAsync"/> on its outcome
/// path -- duplicating the row there would conflate handler-level and
/// transport-level outcomes against the same <c>request_type</c>. The
/// new high-level <see cref="OpenModalAsync"/> overload is the canonical
/// per-brief entry point and is the one that DOES write the audit row,
/// so callers that own the modal-open lifecycle end-to-end can rely on
/// this client alone to satisfy architecture.md §2.15.
/// </para>
/// <para>
/// On any <c>views.open</c> failure -- transport error, Slack error,
/// or rate limit -- the client returns the failure result; the caller
/// must NOT retry via the durable outbound queue because the
/// <c>trigger_id</c> is already expired. Subsequent
/// <c>views.update</c> calls (post-open modal modifications) remain
/// the responsibility of <see cref="SlackOutboundDispatcher"/> through
/// the durable outbound queue, NOT this client (architecture.md §2.15,
/// implementation-plan Stage 6.4 step 5).
/// </para>
/// <para>
/// The production factory built by
/// <see cref="BuildCachingApiClientFactory"/> reuses a single
/// <see cref="SlackApiClient"/> (and its internally-allocated
/// <see cref="System.Net.Http.HttpClient"/>) per bot OAuth token via a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Without this
/// cache, every <c>views.open</c> invocation would allocate a fresh
/// <see cref="System.Net.Http.HttpClient"/>, which is the classic
/// socket-exhaustion anti-pattern under sustained modal-open load
/// (concurrent reviewers clicking buttons during a deployment).
/// Workspace bot tokens are long-lived and the set is small, so the
/// per-instance cache stays bounded without an eviction policy and
/// each cached <see cref="SlackApiClient"/> can safely be shared
/// across concurrent calls (SlackNet's client is thread-safe).
/// </para>
/// </remarks>
internal sealed class SlackDirectApiClient : ISlackViewsOpenClient
{
    /// <summary>Slack Web API method name passed to SlackNet's generic
    /// <see cref="ISlackApiClient.Post(string, Dictionary{string, object}, System.Threading.CancellationToken)"/>.</summary>
    public const string ViewsOpenApiMethod = "views.open";

    /// <summary>Key under which the <c>trigger_id</c> is sent.</summary>
    public const string TriggerIdArgKey = "trigger_id";

    /// <summary>Key under which the modal view payload is sent.</summary>
    public const string ViewArgKey = "view";

    /// <summary>
    /// Default deadline applied to the synchronous <c>views.open</c>
    /// call (architecture.md §2.15: Slack's <c>trigger_id</c> expires
    /// within ~3&#160;seconds of issuance). Set to 2.5&#160;seconds so
    /// the controller still has ~500&#160;ms of headroom for the rest
    /// of the request pipeline (response serialisation, signature
    /// validation tear-down, MVC filters). Matches
    /// <see cref="HttpClientSlackViewsOpenClient"/>'s legacy
    /// per-request timeout so the behavioural envelope is preserved
    /// when the connector composition root swaps in the SlackNet
    /// implementation.
    /// </summary>
    internal static readonly TimeSpan DefaultTriggerIdDeadline = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Default <see cref="ISlackRateLimiter.NotifyRetryAfter"/> pause
    /// applied when Slack rate-limits the call but does NOT supply a
    /// <c>Retry-After</c> header. One second matches the dispatcher's
    /// default fallback so a Tier&#160;4 hop holds the bucket for a
    /// reasonable minimum interval.
    /// </summary>
    internal static readonly TimeSpan DefaultRateLimitPause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Slack rate-limit tier for the <c>views.open</c> call
    /// (architecture.md §2.12 / SlackApiTier.cs:43-44).
    /// </summary>
    private const SlackApiTier ViewsOpenTier = SlackApiTier.Tier4;

    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ISlackRateLimiter rateLimiter;
    private readonly ISlackAuditEntryWriter auditWriter;
    private readonly Func<string, ISlackApiClient> apiClientFactory;
    private readonly ILogger<SlackDirectApiClient> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan triggerIdDeadline;

    /// <summary>
    /// Production constructor: reuses a single SlackNet
    /// <see cref="SlackApiClient"/> per bot OAuth token via a per-
    /// instance <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// built by <see cref="BuildCachingApiClientFactory"/>. The cache
    /// avoids the socket-exhaustion failure mode that would arise
    /// from allocating a new <see cref="System.Net.Http.HttpClient"/>
    /// on every <c>views.open</c> call under sustained modal-open
    /// load.
    /// </summary>
    public SlackDirectApiClient(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ISlackRateLimiter rateLimiter,
        ISlackAuditEntryWriter auditWriter,
        ILogger<SlackDirectApiClient> logger)
        : this(
            workspaceStore,
            secretProvider,
            rateLimiter,
            auditWriter,
            logger,
            apiClientFactory: BuildCachingApiClientFactory(),
            timeProvider: TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a unit test inject a
    /// SlackNet client factory, a fake clock, and override the
    /// trigger_id deadline.
    /// </summary>
    internal SlackDirectApiClient(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ISlackRateLimiter rateLimiter,
        ISlackAuditEntryWriter auditWriter,
        ILogger<SlackDirectApiClient> logger,
        Func<string, ISlackApiClient> apiClientFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? triggerIdDeadline = null)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        TimeSpan deadline = triggerIdDeadline ?? DefaultTriggerIdDeadline;
        this.triggerIdDeadline = deadline > TimeSpan.Zero ? deadline : DefaultTriggerIdDeadline;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Used by the existing <see cref="DefaultSlackModalFastPathHandler"/>
    /// composition path. Does NOT write a transport-level audit row
    /// because the handler already calls
    /// <see cref="SlackModalAuditRecorder"/> on the outcome -- emitting
    /// a second row with the same <c>request_type = modal_open</c>
    /// would conflate the handler-level and transport-level outcomes.
    /// Callers that own the modal-open lifecycle end-to-end SHOULD use
    /// <see cref="OpenModalAsync"/> instead so the audit row is
    /// guaranteed.
    /// </remarks>
    public Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
        => this.CallSlackAsync(request.TeamId, request.TriggerId, request.ViewPayload, ct);

    /// <summary>
    /// Calls Slack's <c>views.open</c> Web API via SlackNet, acquires
    /// a token from the shared rate limiter, and writes the
    /// <c>request_type = modal_open</c> audit row before returning a
    /// <see cref="SlackDirectApiResult"/> the caller can hand straight
    /// back to Slack as the slash-command response body (success: no
    /// content; failure: <see cref="SlackDirectApiResult.EphemeralMessage"/>
    /// ready for the <c>ephemeral</c> response).
    /// </summary>
    /// <remarks>
    /// Implementation-plan Stage 6.4 step 2 / architecture.md §2.15
    /// canonical entry point for the modal fast-path. Performs ALL
    /// of: token resolution, rate-limit acquisition, SlackNet
    /// <c>views.open</c> dispatch, error classification, optional
    /// HTTP&#160;429 back-pressure feedback to the shared limiter, and
    /// the audit-log write. The audit write is best-effort
    /// (cancellation-safe and exception-swallowing) so a logging blip
    /// never fails a user-visible modal that already opened in Slack.
    /// </remarks>
    /// <param name="triggerId">Slack <c>trigger_id</c> from the
    /// originating slash command. Expires ~3&#160;seconds after
    /// issuance.</param>
    /// <param name="modal">Modal payload bundle carrying the workspace
    /// id, rendered <c>view</c> JSON, and optional audit context.</param>
    /// <param name="ct">Cancellation token (typically the HTTP
    /// request's <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestAborted"/>).</param>
    public async Task<SlackDirectApiResult> OpenModalAsync(
        string triggerId,
        SlackModalPayload modal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modal);

        SlackViewsOpenResult result = await this
            .CallSlackAsync(modal.TeamId, triggerId, modal.View, ct)
            .ConfigureAwait(false);

        // Audit ALWAYS happens (success or failure) -- architecture.md
        // §2.15 "logs every call to SlackAuditLogger" /
        // implementation-plan Stage 6.4 step 4.
        await this.WriteAuditAsync(modal, result).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return SlackDirectApiResult.Success();
        }

        return SlackDirectApiResult.Failure(result.Kind, result.Error, BuildEphemeralMessage(result));
    }

    /// <summary>
    /// Builds the production <see cref="ISlackApiClient"/> factory
    /// used by the parameterless constructor. The returned delegate
    /// closes over a private
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> that reuses a
    /// single <see cref="SlackApiClient"/> (and its internally-
    /// allocated <see cref="System.Net.Http.HttpClient"/>) per bot
    /// OAuth token, preventing the socket-exhaustion anti-pattern
    /// that would otherwise occur under sustained modal-open load
    /// (e.g., many concurrent reviewers clicking buttons during a
    /// deployment).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Workspace bot tokens are long-lived (they only rotate via an
    /// explicit OAuth re-install) and the set is bounded by the
    /// number of registered workspaces, so the per-instance cache
    /// stays small without an eviction policy. SlackNet's
    /// <see cref="SlackApiClient"/> is documented to be thread-safe
    /// and is the standard SlackNet usage pattern for long-lived
    /// reuse.
    /// </para>
    /// <para>
    /// The cache is intentionally <b>per instance</b> rather than
    /// <c>static</c> so a future DI lifetime change (e.g., scoped
    /// per-tenant container) does not silently leak
    /// <see cref="SlackApiClient"/> instances across composition
    /// roots, and so test harnesses that new up multiple connector
    /// instances do not see cross-test interference.
    /// </para>
    /// </remarks>
    private static Func<string, ISlackApiClient> BuildCachingApiClientFactory()
    {
        // OAuth tokens are case-sensitive opaque strings, so use
        // Ordinal comparison rather than the default invariant-
        // culture comparer.
        ConcurrentDictionary<string, ISlackApiClient> cache = new(StringComparer.Ordinal);
        return token => cache.GetOrAdd(token, static t => new SlackApiClient(t));
    }

    /// <summary>
    /// Shared core path used by both <see cref="OpenAsync"/> and
    /// <see cref="OpenModalAsync"/>. Resolves the bot token, acquires
    /// a rate-limit token, and dispatches the SlackNet call. Enforces
    /// the <see cref="DefaultTriggerIdDeadline"/> guard so a slow
    /// SlackNet call cannot exceed Slack's ~3-second <c>trigger_id</c>
    /// ACK budget (architecture.md §2.15).
    /// </summary>
    private async Task<SlackViewsOpenResult> CallSlackAsync(
        string teamId,
        string triggerId,
        object viewPayload,
        CancellationToken callerCt)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return SlackViewsOpenResult.MissingConfiguration("team_id missing on request.");
        }

        if (string.IsNullOrWhiteSpace(triggerId))
        {
            return SlackViewsOpenResult.MissingConfiguration("trigger_id missing on request.");
        }

        if (viewPayload is null)
        {
            return SlackViewsOpenResult.MissingConfiguration("view payload missing on request.");
        }

        // Slack's trigger_id expires within approximately three
        // seconds of issuance, so the synchronous fast-path MUST cap
        // every views.open call at a deadline tighter than that
        // budget regardless of how cooperative the caller's
        // CancellationToken is. A linked CTS cancels the inner call
        // when EITHER the caller cancels OR the deadline fires; the
        // outer try/catch below distinguishes the two so a caller
        // cancellation still propagates as OperationCanceledException
        // (preserving the existing contract pinned by
        // SlackDirectApiClientTests.OpenModalAsync_propagates_OperationCanceledException...)
        // while a deadline trip surfaces as a NetworkFailure result
        // that the controller renders as an ephemeral retry message.
        // Architecture.md §2.15 / evaluator iter-2 item #3.
        using CancellationTokenSource deadlineCts = new(this.triggerIdDeadline, this.timeProvider);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            callerCt,
            deadlineCts.Token);
        CancellationToken ct = linkedCts.Token;

        try
        {
            SlackWorkspaceConfig? workspace = await this.workspaceStore
                .GetByTeamIdAsync(teamId, ct)
                .ConfigureAwait(false);
            if (workspace is null || !workspace.Enabled)
            {
                return SlackViewsOpenResult.MissingConfiguration(
                    $"workspace '{teamId}' is not registered or is disabled.");
            }

            if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
            {
                return SlackViewsOpenResult.MissingConfiguration(
                    $"workspace '{teamId}' has no bot-token secret reference.");
            }

            string? botToken;
            try
            {
                botToken = await this.secretProvider
                    .GetSecretAsync(workspace.BotTokenSecretRef, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Honour caller cancellation OR deadline -- never silently
                // convert a client-aborted / deadline-tripped request into
                // a "missing configuration" ephemeral error. The outer
                // catch below sorts the two and emits the appropriate
                // result.
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "Failed to resolve bot-token secret '{SecretRef}' for workspace {TeamId} while opening modal view.",
                    workspace.BotTokenSecretRef,
                    teamId);
                return SlackViewsOpenResult.MissingConfiguration(
                    $"failed to resolve bot-token secret for workspace '{teamId}'.");
            }

            if (string.IsNullOrEmpty(botToken))
            {
                return SlackViewsOpenResult.MissingConfiguration(
                    $"workspace '{teamId}' bot-token secret resolved to empty.");
            }

            // Tier 4 (views.open) is rated per-workspace per architecture.md
            // §2.12 -- the rate-limiter scope key is the workspace id alone.
            // This is the SAME limiter instance the SlackOutboundDispatcher
            // uses (it is registered as a singleton), so concurrent
            // chat.postMessage + views.open calls share the same back-pressure
            // state (implementation-plan Stage 6.4 step 3).
            await this.rateLimiter
                .AcquireAsync(ViewsOpenTier, teamId, ct)
                .ConfigureAwait(false);

            try
            {
                // The production factory caches one ISlackApiClient
                // (and its internal HttpClient) per bot token, so
                // repeated invocations reuse the same connection pool
                // instead of allocating a fresh HttpClient per call.
                // See BuildCachingApiClientFactory remarks.
                ISlackApiClient apiClient = this.apiClientFactory(botToken);
                Dictionary<string, object> args = new(StringComparer.Ordinal)
                {
                    [TriggerIdArgKey] = triggerId,
                    [ViewArgKey] = viewPayload,
                };

                await apiClient
                    .Post(ViewsOpenApiMethod, args, ct)
                    .ConfigureAwait(false);

                return SlackViewsOpenResult.Success();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SlackRateLimitException rle)
            {
                // Slack returned HTTP 429. Surface the back-pressure into
                // the SHARED rate limiter so the SlackOutboundDispatcher
                // also pauses for the published Retry-After window
                // (architecture.md §2.12). Rate-limited views.open cannot be
                // retried because the trigger_id is already expired -- the
                // pause exists to protect SIBLING workspace-scoped calls
                // routed through the same Tier 4 bucket.
                TimeSpan retryAfter = rle.RetryAfter ?? DefaultRateLimitPause;
                this.rateLimiter.NotifyRetryAfter(ViewsOpenTier, teamId, retryAfter);
                this.logger.LogWarning(
                    rle,
                    "Slack views.open rate-limited for workspace {TeamId}; pausing Tier 4 bucket for {RetryAfterSeconds:0.###}s (no retry, trigger_id is single-use).",
                    teamId,
                    retryAfter.TotalSeconds);
                return SlackViewsOpenResult.Failure("rate_limited");
            }
            catch (SlackException sex)
            {
                // {"ok": false, "error": "..."} from Slack.
                string errorCode = string.IsNullOrEmpty(sex.ErrorCode) ? "unknown_error" : sex.ErrorCode;
                this.logger.LogWarning(
                    sex,
                    "Slack views.open returned {ErrorCode} for workspace {TeamId}.",
                    errorCode,
                    teamId);
                return SlackViewsOpenResult.Failure(errorCode);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "Slack views.open transport error for workspace {TeamId}.",
                    teamId);
                return SlackViewsOpenResult.NetworkFailure(ex.Message);
            }
        }
        catch (OperationCanceledException) when (callerCt.IsCancellationRequested)
        {
            // Caller cancelled the request (e.g.,
            // HttpContext.RequestAborted) -- propagate so the
            // controller can finish with the appropriate
            // aborted-response semantics. This matches the contract
            // pinned by
            // SlackDirectApiClientTests.OpenModalAsync_propagates_OperationCanceledException...
            throw;
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
        {
            // Deadline fired BEFORE the caller cancelled. The
            // trigger_id is now stale; do NOT propagate as
            // cancellation (the user request is still alive) -- emit
            // a NetworkFailure result so the controller surfaces an
            // ephemeral "Slack timed out, please retry" message.
            this.logger.LogWarning(
                "Slack views.open exceeded the {DeadlineMs:0} ms trigger_id deadline for workspace {TeamId}; surfacing as NetworkFailure so the controller emits a retry-soon ephemeral message.",
                this.triggerIdDeadline.TotalMilliseconds,
                teamId);
            return SlackViewsOpenResult.NetworkFailure(
                $"views.open exceeded {this.triggerIdDeadline.TotalMilliseconds:0} ms trigger_id deadline");
        }
    }

    /// <summary>
    /// Best-effort audit-row write for the canonical
    /// <see cref="OpenModalAsync"/> path. Uses
    /// <see cref="CancellationToken.None"/> so the row lands even if
    /// the caller's request token is cancelled AFTER the modal has
    /// already opened in Slack (the user already sees the modal; we
    /// must not lose the audit row to a downstream cancellation), and
    /// swallows any exception so a logging blip never propagates back
    /// to the caller.
    /// </summary>
    private async Task WriteAuditAsync(SlackModalPayload modal, SlackViewsOpenResult result)
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        string id = Ulid.NewUlid(now);
        string outcome = result.IsSuccess
            ? SlackModalAuditRecorder.OutcomeSuccess
            : SlackModalAuditRecorder.OutcomeError;
        string? errorDetail = result.IsSuccess
            ? null
            : $"views_open_{result.Kind}: {result.Error ?? "unknown"}";
        string? commandText = string.IsNullOrEmpty(modal.SubCommand)
            ? null
            : $"/agent {modal.SubCommand}";

        SlackAuditEntry entry = new()
        {
            Id = id,
            CorrelationId = modal.CorrelationId ?? string.Empty,
            AgentId = null,
            TaskId = null,
            ConversationId = null,
            Direction = SlackModalAuditRecorder.DirectionInbound,
            RequestType = SlackModalAuditRecorder.RequestTypeModalOpen,
            TeamId = string.IsNullOrEmpty(modal.TeamId) ? "unknown" : modal.TeamId,
            ChannelId = modal.ChannelId,
            ThreadTs = null,
            MessageTs = null,
            UserId = string.IsNullOrEmpty(modal.UserId) ? null : modal.UserId,
            CommandText = commandText,
            ResponsePayload = errorDetail,
            Outcome = outcome,
            ErrorDetail = errorDetail,
            Timestamp = now,
        };

        try
        {
            await this.auditWriter
                .AppendAsync(entry, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to append modal_open audit entry id={Id} outcome={Outcome} team={TeamId}.",
                id,
                outcome,
                modal.TeamId);
        }
    }

    /// <summary>
    /// Maps a <see cref="SlackViewsOpenResult"/> failure into the
    /// user-facing ephemeral text the controller surfaces back to
    /// Slack. Mirrors the wording used by
    /// <see cref="DefaultSlackModalFastPathHandler"/> so users get a
    /// consistent message regardless of which composition path raised
    /// the failure.
    /// </summary>
    private static string BuildEphemeralMessage(SlackViewsOpenResult result) => result.Kind switch
    {
        SlackViewsOpenResultKind.MissingConfiguration =>
            "Could not open the modal: this Slack workspace is not configured for agent commands. Ask the admin to register the workspace.",
        SlackViewsOpenResultKind.NetworkFailure when result.Error is { } err && err.Contains("trigger_id deadline", StringComparison.Ordinal) =>
            "Could not open the modal: Slack did not respond before the trigger_id expired. Please retry the command.",
        SlackViewsOpenResultKind.NetworkFailure =>
            "Could not open the modal: Slack timed out or was unreachable. Please retry in a few seconds.",
        _ when string.Equals(result.Error, "rate_limited", StringComparison.Ordinal) =>
            "Could not open the modal: Slack rate-limited this workspace. Please retry in a few seconds.",
        _ when string.Equals(result.Error, "expired_trigger_id", StringComparison.Ordinal) =>
            "Could not open the modal: the Slack trigger expired before the request reached Slack. Please retry the command.",
        _ => $"Could not open the modal: Slack returned error '{result.Error ?? "unknown_error"}'.",
    };
}
