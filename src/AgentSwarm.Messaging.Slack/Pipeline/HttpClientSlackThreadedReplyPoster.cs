// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackThreadedReplyPoster.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 5.2 production <see cref="ISlackThreadedReplyPoster"/> --
/// resolves the per-workspace bot OAuth token via
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>
/// and POSTs to Slack's <c>chat.postMessage</c> Web API endpoint
/// (<c>https://slack.com/api/chat.postMessage</c>) over a shared
/// <see cref="HttpClient"/>. Used by the
/// <see cref="SlackAppMentionHandler"/> to deliver
/// acknowledgements, status output, and usage hints into the channel /
/// thread where the originating <c>@AgentBot</c> mention was posted.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="Transport.HttpClientSlackViewsOpenClient"/>
/// (Stage 4.1's HTTP-backed views.open client). Architecture.md §2.15
/// names Stage 6.4's <c>SlackDirectApiClient</c> as the eventual
/// consolidated Slack Web API client; Stage 5.2 ships this dedicated
/// client today so the app-mention path produces a real user-visible
/// reply without waiting for Stage 6.x's outbound dispatcher to land.
/// Stage 6.x can supersede this binding via DI pre-registration --
/// the dispatcher extension registers it with <c>TryAddSingleton</c>.
/// </para>
/// <para>
/// Failure semantics follow the <see cref="ISlackThreadedReplyPoster"/>
/// contract: non-fatal HTTP errors (4xx/5xx responses, rate limits,
/// timeouts, malformed JSON) are logged at
/// <see cref="LogLevel.Warning"/> and swallowed because the upstream
/// orchestrator side-effect (e.g.
/// <see cref="AgentSwarm.Messaging.Abstractions.IAgentTaskService.CreateTaskAsync"/>)
/// has already run by the time this is invoked, and retrying the
/// whole envelope would duplicate that side-effect. Caller
/// cancellation (<see cref="OperationCanceledException"/> tied to the
/// supplied <see cref="CancellationToken"/>) IS propagated so the
/// dispatch loop honours shutdown.
/// </para>
/// <para>
/// A fixed 5-second request timeout protects the ingestor's dispatch
/// loop from a stuck Slack call (Slack's documented chat.postMessage
/// budget is much larger than that, but the inbound pipeline is
/// behind an at-least-once queue and a long-stalled post is more
/// observable as a logged timeout than as a backed-up worker).
/// </para>
/// </remarks>
internal sealed class HttpClientSlackThreadedReplyPoster : ISlackThreadedReplyPoster
{
    /// <summary>
    /// Public Slack endpoint for chat.postMessage. Pinned by
    /// Slack's published Web API reference and architecture.md.
    /// </summary>
    public const string ChatPostMessageUrl = "https://slack.com/api/chat.postMessage";

    /// <summary>
    /// Name of the typed <see cref="HttpClient"/> registered via
    /// <see cref="IHttpClientFactory"/>. Allows the host to layer
    /// resilience handlers (retry, circuit-breaker, telemetry) on
    /// top of the default registration without subclassing this
    /// client.
    /// </summary>
    public const string HttpClientName = "slack-chat-postmessage";

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ILogger<HttpClientSlackThreadedReplyPoster> logger;
    private readonly TimeSpan requestTimeout;

    public HttpClientSlackThreadedReplyPoster(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackThreadedReplyPoster> logger)
        : this(httpClientFactory, workspaceStore, secretProvider, logger, DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a unit test override the
    /// request timeout.
    /// </summary>
    public HttpClientSlackThreadedReplyPoster(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackThreadedReplyPoster> logger,
        TimeSpan requestTimeout)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task PostAsync(SlackThreadedReplyRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.TeamId))
        {
            this.logger.LogWarning(
                "HttpClientSlackThreadedReplyPoster skipped reply correlation_id={CorrelationId}: team_id missing on request.",
                request.CorrelationId);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            this.logger.LogWarning(
                "HttpClientSlackThreadedReplyPoster skipped reply correlation_id={CorrelationId} team_id={TeamId}: channel_id missing on request.",
                request.CorrelationId,
                request.TeamId);
            return;
        }

        if (string.IsNullOrEmpty(request.Text))
        {
            return;
        }

        SlackWorkspaceConfig? workspace;
        try
        {
            workspace = await this.workspaceStore
                .GetByTeamIdAsync(request.TeamId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "HttpClientSlackThreadedReplyPoster failed to resolve workspace team_id={TeamId} correlation_id={CorrelationId} -- threaded reply suppressed.",
                request.TeamId,
                request.CorrelationId);
            return;
        }

        if (workspace is null || !workspace.Enabled)
        {
            this.logger.LogWarning(
                "HttpClientSlackThreadedReplyPoster skipped reply correlation_id={CorrelationId}: workspace team_id={TeamId} is not registered or disabled.",
                request.CorrelationId,
                request.TeamId);
            return;
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            this.logger.LogWarning(
                "HttpClientSlackThreadedReplyPoster skipped reply correlation_id={CorrelationId}: workspace team_id={TeamId} has no bot-token secret reference.",
                request.CorrelationId,
                request.TeamId);
            return;
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
            // Honour caller cancellation explicitly -- never convert
            // a client-aborted request into a silently-suppressed
            // missing-secret error.
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "HttpClientSlackThreadedReplyPoster failed to resolve bot-token secret '{SecretRef}' for workspace team_id={TeamId} correlation_id={CorrelationId} -- threaded reply suppressed.",
                workspace.BotTokenSecretRef,
                request.TeamId,
                request.CorrelationId);
            return;
        }

        if (string.IsNullOrEmpty(botToken))
        {
            this.logger.LogWarning(
                "HttpClientSlackThreadedReplyPoster skipped reply correlation_id={CorrelationId}: workspace team_id={TeamId} bot-token secret resolved to empty.",
                request.CorrelationId,
                request.TeamId);
            return;
        }

        // chat.postMessage payload: text + channel + thread_ts (when
        // available). When ThreadTs is null we still post to the
        // channel (top-level), but the Stage 5.2 handler always
        // supplies a non-null value -- thread_ts falls back to the
        // mention's own event.ts so Slack promotes the reply into a
        // new thread.
        object body = string.IsNullOrEmpty(request.ThreadTs)
            ? (object)new
            {
                channel = request.ChannelId,
                text = request.Text,
            }
            : new
            {
                channel = request.ChannelId,
                thread_ts = request.ThreadTs,
                text = request.Text,
            };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, ChatPostMessageUrl)
        {
            Content = JsonContent.Create(body),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using CancellationTokenSource timeoutCts = new(this.requestTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpResponseMessage? httpResponse = null;
        try
        {
            HttpClient httpClient = this.httpClientFactory.CreateClient(HttpClientName);
            httpResponse = await httpClient
                .SendAsync(httpRequest, linked.Token)
                .ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "Slack chat.postMessage returned HTTP {StatusCode} for team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId} -- threaded reply suppressed.",
                    (int)httpResponse.StatusCode,
                    request.TeamId,
                    request.ChannelId,
                    request.ThreadTs,
                    request.CorrelationId);
                return;
            }

            string responseBody = await httpResponse.Content
                .ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                this.logger.LogWarning(
                    "Slack chat.postMessage response was not a JSON object for team_id={TeamId} correlation_id={CorrelationId}.",
                    request.TeamId,
                    request.CorrelationId);
                return;
            }

            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl)
                && okEl.ValueKind == JsonValueKind.True;

            if (ok)
            {
                this.logger.LogInformation(
                    "Slack chat.postMessage posted threaded reply team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId}.",
                    request.TeamId,
                    request.ChannelId,
                    request.ThreadTs,
                    request.CorrelationId);
                return;
            }

            string slackError = "unknown_error";
            if (doc.RootElement.TryGetProperty("error", out JsonElement errEl)
                && errEl.ValueKind == JsonValueKind.String)
            {
                slackError = errEl.GetString() ?? "unknown_error";
            }

            this.logger.LogWarning(
                "Slack chat.postMessage returned {{ok:false, error:'{SlackError}'}} for team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId} -- threaded reply suppressed.",
                slackError,
                request.TeamId,
                request.ChannelId,
                request.ThreadTs,
                request.CorrelationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation propagates; the dispatch loop needs
            // to honour shutdown rather than silently swallow.
            throw;
        }
        catch (OperationCanceledException)
        {
            this.logger.LogWarning(
                "Slack chat.postMessage timed out after {TimeoutMs} ms for team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId} -- threaded reply suppressed.",
                this.requestTimeout.TotalMilliseconds,
                request.TeamId,
                request.ChannelId,
                request.ThreadTs,
                request.CorrelationId);
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.postMessage transport error for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId} -- threaded reply suppressed.",
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.postMessage response body was malformed JSON for team_id={TeamId} correlation_id={CorrelationId}.",
                request.TeamId,
                request.CorrelationId);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.postMessage failed unexpectedly for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId} -- threaded reply suppressed.",
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
        }
        finally
        {
            httpResponse?.Dispose();
        }
    }
}
