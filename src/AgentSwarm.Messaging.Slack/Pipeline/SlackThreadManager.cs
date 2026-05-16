// -----------------------------------------------------------------------
// <copyright file="SlackThreadManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Identifiers;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 6.2 default <see cref="ISlackThreadManager"/>: persists the
/// one-to-one mapping between an agent <c>task_id</c> and the Slack
/// thread it owns, creates the thread root via
/// <see cref="ISlackChatPostMessageClient"/> on first call, posts
/// threaded replies via the same client, and recovers into the
/// workspace's <see cref="SlackWorkspaceConfig.FallbackChannelId"/>
/// when the previously-stored channel / thread is no longer reachable.
/// </summary>
/// <typeparam name="TContext">EF Core context implementing
/// <see cref="ISlackThreadMappingDbContext"/>. The Worker host's
/// <see cref="SlackPersistenceDbContext"/> satisfies the constraint.
/// </typeparam>
/// <remarks>
/// <para>
/// Architecture.md §2.11 lifecycle, mapped to the implementation steps
/// on Stage 6.2:
/// <list type="number">
///   <item><description><b>Step 1</b> -- root creation in
///     <see cref="GetOrCreateThreadAsync"/>; the channel is resolved
///     from <see cref="SlackWorkspaceConfig.DefaultChannelId"/> -- the
///     manager does NOT accept a caller-supplied channel id so callers
///     cannot bypass workspace configuration.</description></item>
///   <item><description><b>Step 2</b> -- threaded replies in
///     <see cref="PostThreadedReplyAsync"/> route through the same
///     <see cref="ISlackChatPostMessageClient"/> with
///     <see cref="SlackChatPostMessageRequest.ThreadTs"/> set so the
///     message lands in the owning thread.</description></item>
///   <item><description><b>Step 3</b> -- restart continuity is automatic
///     because <see cref="GetOrCreateThreadAsync"/> always reads the
///     mapping table first; a fresh manager instance over a populated
///     database resolves the existing row without posting.</description></item>
///   <item><description><b>Step 4</b> -- fallback recovery is performed
///     inline by <see cref="GetOrCreateThreadAsync"/> on initial-root
///     failure (e2e-scenarios.md scenario 13.3) AND by
///     <see cref="PostThreadedReplyAsync"/> on threaded-reply failure;
///     <see cref="RecoverThreadAsync"/> remains as a dedicated entry
///     point for the dispatcher.</description></item>
///   <item><description><b>Step 5 / 6</b> --
///     <see cref="SlackThreadMapping.LastMessageAt"/> is bumped on
///     <see cref="GetOrCreateThreadAsync"/> (root post),
///     <see cref="PostThreadedReplyAsync"/> (every threaded reply), and
///     <see cref="RecoverThreadAsync"/> (replacement root post). The
///     bump is intrinsic to those production paths, so the requirement
///     "update <c>LastMessageAt</c> on every new message" cannot be
///     silently skipped by a caller that talks to the chat client
///     directly.</description></item>
/// </list>
/// </para>
/// <para>
/// The manager is safe to register as a <em>singleton</em>: it resolves
/// the scoped <typeparamref name="TContext"/> from a fresh
/// <see cref="IServiceScope"/> per call. This matches the pattern used
/// by <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/> and
/// <see cref="EntityFrameworkSlackWorkspaceConfigStore{TContext}"/>.
/// </para>
/// <para>
/// The root-message text template is intentionally fixed at this stage:
/// "Agent task <c>{taskId}</c> started. (correlation_id: <c>{correlationId}</c>)".
/// Stage 6.3's outbound dispatcher will eventually replace the
/// placeholder root with the task summary via <c>chat.update</c>, but
/// the deterministic template here is sufficient to satisfy the
/// e2e-scenario assertion that the root text contains the task id and
/// correlation id (architecture.md §5.1 step 6 + e2e-scenarios.md
/// scenario 13.1).
/// </para>
/// </remarks>
internal sealed class SlackThreadManager<TContext> : ISlackThreadManager
    where TContext : DbContext, ISlackThreadMappingDbContext
{
    /// <summary><see cref="SlackAuditEntry.Direction"/> value for thread-manager rows.</summary>
    public const string DirectionOutbound = "outbound";

    /// <summary>
    /// <see cref="SlackAuditEntry.RequestType"/> for the root
    /// <c>chat.postMessage</c> emitted by
    /// <see cref="GetOrCreateThreadAsync"/>.
    /// </summary>
    public const string RequestTypeThreadCreate = "thread_create";

    /// <summary>
    /// <see cref="SlackAuditEntry.RequestType"/> for the fallback-channel
    /// recovery emitted by <see cref="RecoverThreadAsync"/>.
    /// </summary>
    public const string RequestTypeThreadRecover = "thread_recover";

    /// <summary>
    /// <see cref="SlackAuditEntry.RequestType"/> for threaded replies
    /// emitted by <see cref="PostThreadedReplyAsync"/>.
    /// </summary>
    public const string RequestTypeThreadMessage = "thread_message";

    /// <summary>Outcome marker: thread was created or reused successfully.</summary>
    public const string OutcomeSuccess = "success";

    /// <summary>Outcome marker: recovery succeeded in the fallback channel.</summary>
    public const string OutcomeFallbackUsed = "fallback_used";

    /// <summary>Outcome marker: the operation failed (no fallback, post error, etc.).</summary>
    public const string OutcomeError = "error";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISlackChatPostMessageClient chatClient;
    private readonly ISlackAuditEntryWriter auditWriter;
    private readonly ILogger<SlackThreadManager<TContext>> logger;
    private readonly TimeProvider timeProvider;

    public SlackThreadManager(
        IServiceScopeFactory scopeFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackChatPostMessageClient chatClient,
        ISlackAuditEntryWriter auditWriter,
        ILogger<SlackThreadManager<TContext>> logger)
        : this(scopeFactory, workspaceStore, chatClient, auditWriter, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets the fixture inject a fake
    /// <see cref="TimeProvider"/> so <see cref="SlackThreadMapping.CreatedAt"/>
    /// / <see cref="SlackThreadMapping.LastMessageAt"/> are deterministic.
    /// </summary>
    internal SlackThreadManager(
        IServiceScopeFactory scopeFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackChatPostMessageClient chatClient,
        ISlackAuditEntryWriter auditWriter,
        ILogger<SlackThreadManager<TContext>> logger,
        TimeProvider timeProvider)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<SlackThreadMapping> GetOrCreateThreadAsync(
        string taskId,
        string agentId,
        string correlationId,
        string teamId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("taskId must be non-empty.", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(teamId))
        {
            throw new ArgumentException("teamId must be non-empty.", nameof(teamId));
        }

        ct.ThrowIfCancellationRequested();

        // Lifecycle step 3: restart continuity. A populated mapping for
        // this taskId means a previous run (possibly before a process
        // restart) already created the thread; reuse it unconditionally
        // so we do NOT post a duplicate root message.
        SlackThreadMapping? existing = await this.LookupAsync(taskId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            this.logger.LogDebug(
                "SlackThreadManager reused existing mapping task_id={TaskId} team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId}.",
                existing.TaskId,
                existing.TeamId,
                existing.ChannelId,
                existing.ThreadTs,
                correlationId);
            return existing;
        }

        // Resolve channel from workspace config -- Stage 6.2 step 2:
        // "post a root status message to the workspace's DefaultChannelId".
        // The manager owns this lookup so callers cannot accidentally
        // bypass workspace allow-listing by hand-picking a channel.
        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(teamId, ct)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            string detail = $"workspace '{teamId}' is not registered or is disabled.";
            await this.WriteAuditAsync(
                outcome: OutcomeError, requestType: RequestTypeThreadCreate,
                taskId: taskId, agentId: agentId, correlationId: correlationId,
                teamId: teamId, channelId: null, threadTs: null, messageTs: null,
                errorDetail: detail, ct).ConfigureAwait(false);
            throw new SlackThreadCreationException(
                taskId, teamId, channelId: "(unresolved)",
                SlackChatPostMessageResultKind.MissingConfiguration, detail);
        }

        if (string.IsNullOrWhiteSpace(workspace.DefaultChannelId))
        {
            string detail = $"workspace '{teamId}' has no default_channel_id configured.";
            await this.WriteAuditAsync(
                outcome: OutcomeError, requestType: RequestTypeThreadCreate,
                taskId: taskId, agentId: agentId, correlationId: correlationId,
                teamId: teamId, channelId: null, threadTs: null, messageTs: null,
                errorDetail: detail, ct).ConfigureAwait(false);
            throw new SlackThreadCreationException(
                taskId, teamId, channelId: "(unresolved)",
                SlackChatPostMessageResultKind.MissingConfiguration, detail);
        }

        string defaultChannel = workspace.DefaultChannelId;
        string text = RenderRootText(taskId, correlationId);

        // Lifecycle step 1: post the root message into the default channel.
        SlackChatPostMessageResult firstResult = await this.chatClient
            .PostAsync(new SlackChatPostMessageRequest(teamId, defaultChannel, text, correlationId), ct)
            .ConfigureAwait(false);

        bool usedFallback = false;
        SlackChatPostMessageResult winningResult = firstResult;
        string winningChannel = defaultChannel;

        // Lifecycle step 4: if the default channel can no longer be
        // posted into (deleted root, archived channel, bot evicted),
        // attempt the workspace's fallback channel INLINE. This is the
        // e2e-scenarios.md scenario 13.3 path -- callers should not need
        // a separate "recover" branch to satisfy the brief; the manager
        // handles the recovery transparently when a fallback exists.
        if (!firstResult.IsSuccess || string.IsNullOrEmpty(firstResult.Ts))
        {
            if (firstResult.IsChannelMissing
                && !string.IsNullOrWhiteSpace(workspace.FallbackChannelId)
                && !string.Equals(workspace.FallbackChannelId, defaultChannel, StringComparison.Ordinal))
            {
                this.logger.LogWarning(
                    "SlackThreadManager root post failed task_id={TaskId} team_id={TeamId} default_channel_id={DefaultChannel} error={Error} -- retrying on fallback_channel_id={FallbackChannel}.",
                    taskId,
                    teamId,
                    defaultChannel,
                    firstResult.Error,
                    workspace.FallbackChannelId);

                string fallbackChannel = workspace.FallbackChannelId!;
                SlackChatPostMessageResult fallbackResult = await this.chatClient
                    .PostAsync(new SlackChatPostMessageRequest(teamId, fallbackChannel, text, correlationId), ct)
                    .ConfigureAwait(false);

                if (fallbackResult.IsSuccess && !string.IsNullOrEmpty(fallbackResult.Ts))
                {
                    winningResult = fallbackResult;
                    winningChannel = fallbackResult.Channel ?? fallbackChannel;
                    usedFallback = true;
                }
                else
                {
                    string fallbackDetail = fallbackResult.Error ?? "no error detail";
                    await this.WriteAuditAsync(
                        outcome: OutcomeError, requestType: RequestTypeThreadCreate,
                        taskId: taskId, agentId: agentId, correlationId: correlationId,
                        teamId: teamId, channelId: fallbackChannel,
                        threadTs: null, messageTs: null,
                        errorDetail: $"fallback_post_failed: {fallbackDetail}",
                        ct).ConfigureAwait(false);
                    throw new SlackThreadCreationException(
                        taskId, teamId, fallbackChannel,
                        fallbackResult.Kind, fallbackDetail);
                }
            }
            else
            {
                // Non-recoverable error OR no fallback configured.
                string errorDetail = firstResult.Error ?? "no error detail";
                await this.WriteAuditAsync(
                    outcome: OutcomeError, requestType: RequestTypeThreadCreate,
                    taskId: taskId, agentId: agentId, correlationId: correlationId,
                    teamId: teamId, channelId: defaultChannel,
                    threadTs: null, messageTs: null,
                    errorDetail: errorDetail, ct).ConfigureAwait(false);
                throw new SlackThreadCreationException(
                    taskId, teamId, defaultChannel, firstResult.Kind, errorDetail);
            }
        }
        else
        {
            winningChannel = firstResult.Channel ?? defaultChannel;
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        SlackThreadMapping mapping = new()
        {
            TaskId = taskId,
            TeamId = teamId,
            ChannelId = winningChannel,
            ThreadTs = winningResult.Ts!,
            CorrelationId = correlationId ?? string.Empty,
            AgentId = agentId ?? string.Empty,
            CreatedAt = now,
            LastMessageAt = now,
        };

        try
        {
            await this.PersistNewMappingAsync(mapping, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException duplicate)
        {
            // A concurrent caller raced ahead of us between the lookup
            // and the insert. The thread mapping table has TaskId as
            // its primary key, so the second insert hits a duplicate-key
            // exception. Re-read the winner and return it. The root
            // message we already posted is now orphaned (no mapping
            // points at it) -- log the resource leak but do NOT throw,
            // because the caller's contract is "return a mapping" and
            // the winner's mapping is perfectly usable.
            this.logger.LogWarning(
                duplicate,
                "SlackThreadManager lost insert race for task_id={TaskId}; reusing winner. The pre-empted root message at ts={Ts} is now orphaned.",
                taskId,
                winningResult.Ts);

            SlackThreadMapping? winner = await this.LookupAsync(taskId, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        await this.WriteAuditAsync(
            outcome: usedFallback ? OutcomeFallbackUsed : OutcomeSuccess,
            requestType: RequestTypeThreadCreate,
            taskId: taskId, agentId: agentId, correlationId: correlationId,
            teamId: teamId, channelId: mapping.ChannelId,
            threadTs: mapping.ThreadTs, messageTs: mapping.ThreadTs,
            errorDetail: null, ct).ConfigureAwait(false);

        if (usedFallback)
        {
            this.logger.LogWarning(
                "SlackThreadManager created thread on FALLBACK channel task_id={TaskId} team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId}.",
                mapping.TaskId, mapping.TeamId, mapping.ChannelId, mapping.ThreadTs, correlationId);
        }
        else
        {
            this.logger.LogInformation(
                "SlackThreadManager created thread task_id={TaskId} team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId}.",
                mapping.TaskId, mapping.TeamId, mapping.ChannelId, mapping.ThreadTs, correlationId);
        }

        return mapping;
    }

    /// <inheritdoc />
    public Task<SlackThreadMapping?> GetThreadAsync(string taskId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult<SlackThreadMapping?>(null);
        }

        ct.ThrowIfCancellationRequested();
        return this.LookupAsync(taskId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(string taskId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();

        SlackThreadMapping? bumped = await this.BumpLastMessageAtAsync(taskId, ct).ConfigureAwait(false);
        return bumped is not null;
    }

    /// <inheritdoc />
    public async Task<SlackThreadMapping?> RecoverThreadAsync(
        string taskId,
        string agentId,
        string correlationId,
        string teamId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("taskId must be non-empty.", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(teamId))
        {
            throw new ArgumentException("teamId must be non-empty.", nameof(teamId));
        }

        ct.ThrowIfCancellationRequested();

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(teamId, ct)
            .ConfigureAwait(false);

        if (workspace is null || string.IsNullOrWhiteSpace(workspace.FallbackChannelId))
        {
            await this.WriteAuditAsync(
                outcome: OutcomeError, requestType: RequestTypeThreadRecover,
                taskId: taskId, agentId: agentId, correlationId: correlationId,
                teamId: teamId, channelId: null, threadTs: null, messageTs: null,
                errorDetail: workspace is null
                    ? $"workspace '{teamId}' is not registered."
                    : "workspace has no fallback_channel_id configured.",
                ct).ConfigureAwait(false);

            this.logger.LogWarning(
                "SlackThreadManager cannot recover thread task_id={TaskId} team_id={TeamId} correlation_id={CorrelationId}: no fallback channel configured.",
                taskId, teamId, correlationId);

            return null;
        }

        string fallbackChannel = workspace.FallbackChannelId!;
        string text = RenderRootText(taskId, correlationId);

        SlackChatPostMessageResult result = await this.chatClient
            .PostAsync(new SlackChatPostMessageRequest(teamId, fallbackChannel, text, correlationId), ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess || string.IsNullOrEmpty(result.Ts))
        {
            string errorDetail = result.Error ?? "no error detail";
            await this.WriteAuditAsync(
                outcome: OutcomeError, requestType: RequestTypeThreadRecover,
                taskId: taskId, agentId: agentId, correlationId: correlationId,
                teamId: teamId, channelId: fallbackChannel,
                threadTs: null, messageTs: null,
                errorDetail: errorDetail, ct).ConfigureAwait(false);

            this.logger.LogWarning(
                "SlackThreadManager failed to recover thread task_id={TaskId} team_id={TeamId} fallback_channel_id={FallbackChannel} correlation_id={CorrelationId} result_kind={ResultKind} error={Error}.",
                taskId, teamId, fallbackChannel, correlationId, result.Kind, errorDetail);

            return null;
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        SlackThreadMapping mapping = await this.UpsertRecoveredMappingAsync(
                taskId, teamId, agentId, correlationId,
                result.Channel ?? fallbackChannel, result.Ts!, now, ct)
            .ConfigureAwait(false);

        await this.WriteAuditAsync(
            outcome: OutcomeFallbackUsed, requestType: RequestTypeThreadRecover,
            taskId: taskId, agentId: agentId, correlationId: correlationId,
            teamId: teamId, channelId: mapping.ChannelId,
            threadTs: mapping.ThreadTs, messageTs: mapping.ThreadTs,
            errorDetail: null, ct).ConfigureAwait(false);

        this.logger.LogWarning(
            "SlackThreadManager recovered thread task_id={TaskId} team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId} via fallback channel.",
            mapping.TaskId, mapping.TeamId, mapping.ChannelId, mapping.ThreadTs, correlationId);

        return mapping;
    }

    /// <inheritdoc />
    public async Task<SlackThreadPostResult> PostThreadedReplyAsync(
        string taskId,
        string text,
        string? correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("taskId must be non-empty.", nameof(taskId));
        }

        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("text must be non-empty.", nameof(text));
        }

        ct.ThrowIfCancellationRequested();

        SlackThreadMapping? mapping = await this.LookupAsync(taskId, ct).ConfigureAwait(false);
        if (mapping is null)
        {
            await this.WriteAuditAsync(
                outcome: OutcomeError, requestType: RequestTypeThreadMessage,
                taskId: taskId, agentId: null, correlationId: correlationId,
                teamId: "unknown", channelId: null, threadTs: null, messageTs: null,
                errorDetail: "no thread mapping exists.", ct).ConfigureAwait(false);
            return SlackThreadPostResult.MappingMissing(taskId);
        }

        SlackChatPostMessageResult result = await this.chatClient
            .PostAsync(
                new SlackChatPostMessageRequest(
                    mapping.TeamId,
                    mapping.ChannelId,
                    text,
                    correlationId ?? mapping.CorrelationId,
                    mapping.ThreadTs),
                ct)
            .ConfigureAwait(false);

        if (result.IsSuccess && !string.IsNullOrEmpty(result.Ts))
        {
            // Single scope: load the tracked entity, bump
            // LastMessageAt, save, and return the updated row. This
            // replaces the earlier TouchAsync + LookupAsync pair, which
            // opened two extra scopes (one to bump, one to re-read) on
            // every successful threaded reply. The bumped entity is
            // returned directly so the audit row and the
            // SlackThreadPostResult observe the new timestamp without
            // a redundant round-trip.
            SlackThreadMapping refreshed =
                await this.BumpLastMessageAtAsync(taskId, ct).ConfigureAwait(false)
                ?? mapping;

            await this.WriteAuditAsync(
                outcome: OutcomeSuccess, requestType: RequestTypeThreadMessage,
                taskId: taskId, agentId: mapping.AgentId,
                correlationId: correlationId ?? mapping.CorrelationId,
                teamId: mapping.TeamId, channelId: mapping.ChannelId,
                threadTs: mapping.ThreadTs, messageTs: result.Ts,
                errorDetail: null, ct).ConfigureAwait(false);
            return SlackThreadPostResult.Posted(refreshed, result.Ts!);
        }

        // Recovery path: if the persisted thread is gone, recreate on
        // the fallback channel and retry the reply once. This is what
        // makes "update LastMessageAt on every new message" robust in
        // the face of archived channels -- the manager owns the retry.
        if (result.IsChannelMissing)
        {
            this.logger.LogWarning(
                "SlackThreadManager threaded reply failed task_id={TaskId} team_id={TeamId} channel_id={ChannelId} error={Error} -- attempting fallback recovery.",
                taskId, mapping.TeamId, mapping.ChannelId, result.Error);

            SlackThreadMapping? recovered = await this.RecoverThreadAsync(
                    taskId, mapping.AgentId, correlationId ?? mapping.CorrelationId, mapping.TeamId, ct)
                .ConfigureAwait(false);

            if (recovered is null)
            {
                return SlackThreadPostResult.Failed(mapping, result.Error ?? "recovery_failed");
            }

            SlackChatPostMessageResult retry = await this.chatClient
                .PostAsync(
                    new SlackChatPostMessageRequest(
                        recovered.TeamId,
                        recovered.ChannelId,
                        text,
                        correlationId ?? recovered.CorrelationId,
                        recovered.ThreadTs),
                    ct)
                .ConfigureAwait(false);

            if (retry.IsSuccess && !string.IsNullOrEmpty(retry.Ts))
            {
                // Same single-scope consolidation as the happy path
                // above: avoid a separate TouchAsync + re-LookupAsync
                // on the retry-after-recovery branch.
                SlackThreadMapping refreshed =
                    await this.BumpLastMessageAtAsync(taskId, ct).ConfigureAwait(false)
                    ?? recovered;

                await this.WriteAuditAsync(
                    outcome: OutcomeFallbackUsed, requestType: RequestTypeThreadMessage,
                    taskId: taskId, agentId: recovered.AgentId,
                    correlationId: correlationId ?? recovered.CorrelationId,
                    teamId: recovered.TeamId, channelId: recovered.ChannelId,
                    threadTs: recovered.ThreadTs, messageTs: retry.Ts,
                    errorDetail: null, ct).ConfigureAwait(false);
                return SlackThreadPostResult.Recovered(refreshed, retry.Ts!);
            }

            string retryDetail = retry.Error ?? "no error detail";
            await this.WriteAuditAsync(
                outcome: OutcomeError, requestType: RequestTypeThreadMessage,
                taskId: taskId, agentId: recovered.AgentId,
                correlationId: correlationId ?? recovered.CorrelationId,
                teamId: recovered.TeamId, channelId: recovered.ChannelId,
                threadTs: recovered.ThreadTs, messageTs: null,
                errorDetail: $"retry_after_recovery_failed: {retryDetail}", ct).ConfigureAwait(false);
            return SlackThreadPostResult.Failed(recovered, retryDetail);
        }

        // Non-recoverable failure -- e.g., rate_limited, internal_error,
        // network/transport failure. Caller decides whether to retry or
        // dead-letter the originating outbound message.
        string detail = result.Error ?? "no error detail";
        await this.WriteAuditAsync(
            outcome: OutcomeError, requestType: RequestTypeThreadMessage,
            taskId: taskId, agentId: mapping.AgentId,
            correlationId: correlationId ?? mapping.CorrelationId,
            teamId: mapping.TeamId, channelId: mapping.ChannelId,
            threadTs: mapping.ThreadTs, messageTs: null,
            errorDetail: detail, ct).ConfigureAwait(false);
        return SlackThreadPostResult.Failed(mapping, detail);
    }

    private static string RenderRootText(string taskId, string? correlationId)
    {
        string corr = string.IsNullOrEmpty(correlationId) ? "(none)" : correlationId!;
        return $"Agent task `{taskId}` started. (correlation_id: `{corr}`)";
    }

    private async Task<SlackThreadMapping?> LookupAsync(string taskId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        return await context.SlackThreadMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.TaskId == taskId, ct)
            .ConfigureAwait(false);
    }

    private async Task PersistNewMappingAsync(SlackThreadMapping mapping, CancellationToken ct)
    {
        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        context.SlackThreadMappings.Add(mapping);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a single EF scope, loads the mapping for <paramref name="taskId"/>
    /// as a tracked entity, bumps <see cref="SlackThreadMapping.LastMessageAt"/>
    /// to the current UTC time, saves, and returns the updated row.
    /// Returns <see langword="null"/> when no mapping exists for
    /// <paramref name="taskId"/>. The caller-observed entity is a
    /// detached snapshot once the scope's DbContext is disposed --
    /// callers only consume the data, never re-attach it.
    /// </summary>
    /// <remarks>
    /// Replaces the previous "TouchAsync (open scope, load tracked,
    /// save) + LookupAsync (open scope, AsNoTracking read)" pair that
    /// PostThreadedReplyAsync used on every successful send: one scope
    /// instead of two, no redundant re-read, and the returned entity
    /// is guaranteed to carry the freshly-bumped timestamp without an
    /// extra round-trip.
    /// </remarks>
    private async Task<SlackThreadMapping?> BumpLastMessageAtAsync(string taskId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        SlackThreadMapping? row = await context.SlackThreadMappings
            .FirstOrDefaultAsync(m => m.TaskId == taskId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        if (row.LastMessageAt < now)
        {
            // Avoid a pointless UPDATE when an in-flight caller already
            // bumped past 'now' (can happen with a coarse-grained
            // TimeProvider in tests, or when two threaded replies land
            // in the same tick). The row we hand back to the caller
            // still reflects the latest persisted state.
            row.LastMessageAt = now;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return row;
    }

    private async Task<SlackThreadMapping> UpsertRecoveredMappingAsync(
        string taskId,
        string teamId,
        string agentId,
        string correlationId,
        string channelId,
        string threadTs,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        SlackThreadMapping? existing = await context.SlackThreadMappings
            .FirstOrDefaultAsync(m => m.TaskId == taskId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            SlackThreadMapping inserted = new()
            {
                TaskId = taskId,
                TeamId = teamId,
                ChannelId = channelId,
                ThreadTs = threadTs,
                CorrelationId = correlationId ?? string.Empty,
                AgentId = agentId ?? string.Empty,
                CreatedAt = now,
                LastMessageAt = now,
            };

            context.SlackThreadMappings.Add(inserted);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return inserted;
        }

        existing.TeamId = teamId;
        existing.ChannelId = channelId;
        existing.ThreadTs = threadTs;

        // CorrelationId / AgentId are preserved from the original
        // mapping so a recovered thread still carries the task's
        // canonical correlation anchor in the audit log. CreatedAt
        // intentionally stays as the original create time -- the
        // recovery is documented through the audit row, not by
        // rewriting the mapping's birth date.
        if (string.IsNullOrEmpty(existing.CorrelationId) && !string.IsNullOrEmpty(correlationId))
        {
            existing.CorrelationId = correlationId!;
        }

        if (string.IsNullOrEmpty(existing.AgentId) && !string.IsNullOrEmpty(agentId))
        {
            existing.AgentId = agentId!;
        }

        existing.LastMessageAt = now;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    private async Task WriteAuditAsync(
        string outcome,
        string requestType,
        string taskId,
        string? agentId,
        string? correlationId,
        string teamId,
        string? channelId,
        string? threadTs,
        string? messageTs,
        string? errorDetail,
        CancellationToken ct)
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        string id = Ulid.NewUlid(now);

        SlackAuditEntry entry = new()
        {
            Id = id,
            CorrelationId = string.IsNullOrEmpty(correlationId) ? id : correlationId!,
            AgentId = string.IsNullOrEmpty(agentId) ? null : agentId,
            TaskId = taskId,
            ConversationId = string.IsNullOrEmpty(threadTs) ? channelId : threadTs,
            Direction = DirectionOutbound,
            RequestType = requestType,
            TeamId = string.IsNullOrEmpty(teamId) ? "unknown" : teamId,
            ChannelId = channelId,
            ThreadTs = threadTs,
            MessageTs = messageTs,
            UserId = null,
            CommandText = null,
            ResponsePayload = null,
            Outcome = outcome,
            ErrorDetail = string.Equals(outcome, OutcomeError, StringComparison.Ordinal)
                ? errorDetail
                : null,
            Timestamp = now,
        };

        try
        {
            await this.auditWriter.AppendAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort audit: never let a logging blip take down the
            // outbound dispatch loop. Mirrors the swallow-and-log
            // pattern used by SlackInboundAuditRecorder.
            this.logger.LogError(
                ex,
                "Failed to append Slack thread-manager audit entry id={Id} outcome={Outcome} request_type={RequestType} task_id={TaskId} team_id={TeamId}.",
                id, outcome, requestType, taskId, teamId);
        }
    }
}

/// <summary>
/// Thrown by
/// <see cref="SlackThreadManager{TContext}.GetOrCreateThreadAsync"/>
/// when the Slack <c>chat.postMessage</c> call required to establish
/// a new thread root fails AND no usable fallback channel exists (or
/// the fallback also fails). The caller (Stage 6.3's outbound
/// dispatcher) catches this and routes the originating outbound
/// message back through the retry / dead-letter machinery; the
/// thread manager intentionally does NOT manufacture a synthetic
/// mapping for a non-existent root message.
/// </summary>
[Serializable]
internal sealed class SlackThreadCreationException : Exception
{
    /// <summary>Initialises a new <see cref="SlackThreadCreationException"/>.</summary>
    public SlackThreadCreationException(
        string taskId,
        string teamId,
        string channelId,
        SlackChatPostMessageResultKind resultKind,
        string error)
        : base($"Failed to create Slack thread for task_id='{taskId}' team_id='{teamId}' channel_id='{channelId}': {resultKind} ({error}).")
    {
        this.TaskId = taskId;
        this.TeamId = teamId;
        this.ChannelId = channelId;
        this.ResultKind = resultKind;
        this.SlackError = error;
    }

    /// <summary>Agent task identifier the thread creation was for.</summary>
    public string TaskId { get; }

    /// <summary>Slack workspace id.</summary>
    public string TeamId { get; }

    /// <summary>Slack channel the root post was attempted in.</summary>
    public string ChannelId { get; }

    /// <summary>
    /// Classified outcome of the failed <c>chat.postMessage</c> call so
    /// the catching dispatcher can branch on the enum directly (recover
    /// vs. retry vs. dead-letter) without an <c>int</c>-to-enum cast.
    /// </summary>
    public SlackChatPostMessageResultKind ResultKind { get; }

    /// <summary>Slack-reported error string (e.g. <c>channel_not_found</c>).</summary>
    public string SlackError { get; }
}
