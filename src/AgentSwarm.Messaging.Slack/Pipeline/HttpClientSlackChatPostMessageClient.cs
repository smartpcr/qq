// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackChatPostMessageClient.cs" company="Microsoft Corp.">
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
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 6.2 default <see cref="ISlackChatPostMessageClient"/>:
/// resolves the per-workspace bot OAuth token via
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>
/// and POSTs to Slack's <c>chat.postMessage</c> endpoint
/// (<c>https://slack.com/api/chat.postMessage</c>) over a shared
/// <see cref="HttpClient"/>. Returns the parsed <c>ts</c> + Slack
/// error string so <see cref="SlackThreadManager{TContext}"/> can
/// branch on <c>channel_not_found</c> / <c>is_archived</c> /
/// <c>not_in_channel</c> / <c>message_not_found</c> and trigger
/// fallback recovery (architecture.md §2.11 lifecycle step 4).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="HttpClientSlackChatUpdateClient"/>: the
/// failure-mode handling is identical (missing configuration vs Slack
/// error vs transport failure are all returned, never thrown, so the
/// caller can write a single audit line without aborting the
/// surrounding work). Stage 6.4's <c>SlackDirectApiClient</c> is
/// expected to supersede this client; until then this is what runs in
/// production.
/// </para>
/// <para>
/// Per-request timeout is sourced from
/// <see cref="SlackChatPostMessageClientOptions.RequestTimeout"/>; the
/// default (10&#160;seconds) is wider than the Stage 5.2 reply poster's
/// 5-second timeout because root-message creation runs on Stage 6.3's
/// background dispatch loop where occasional network variance does not
/// erode an interactive ACK budget. Hosts can tune the timeout via the
/// <c>"Slack:ChatPostMessage"</c> configuration section.
/// </para>
/// </remarks>
internal sealed class HttpClientSlackChatPostMessageClient : ISlackChatPostMessageClient
{
    /// <summary>Slack endpoint for <c>chat.postMessage</c>.</summary>
    public const string ChatPostMessageUrl = "https://slack.com/api/chat.postMessage";

    /// <summary>Named <see cref="HttpClient"/> for resilience-handler layering.</summary>
    public const string HttpClientName = "slack-thread-postmessage";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ILogger<HttpClientSlackChatPostMessageClient> logger;
    private readonly TimeSpan requestTimeout;

    /// <summary>
    /// DI-friendly constructor. Resolves the request timeout from
    /// <see cref="IOptions{TOptions}"/> of
    /// <see cref="SlackChatPostMessageClientOptions"/> so operators can
    /// override the default through configuration without recompiling.
    /// </summary>
    public HttpClientSlackChatPostMessageClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackChatPostMessageClient> logger,
        IOptions<SlackChatPostMessageClientOptions> options)
        : this(
            httpClientFactory,
            workspaceStore,
            secretProvider,
            logger,
            (options ?? throw new ArgumentNullException(nameof(options))).Value?.RequestTimeout
                ?? SlackChatPostMessageClientOptions.DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that pins the per-request timeout
    /// directly. Visible to the Slack test assembly via
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    internal HttpClientSlackChatPostMessageClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackChatPostMessageClient> logger,
        TimeSpan requestTimeout)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.requestTimeout = requestTimeout > TimeSpan.Zero
            ? requestTimeout
            : SlackChatPostMessageClientOptions.DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task<SlackChatPostMessageResult> PostAsync(
        SlackChatPostMessageRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.TeamId))
        {
            return SlackChatPostMessageResult.MissingConfiguration("team_id missing on request.");
        }

        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            return SlackChatPostMessageResult.Skipped("channel_id missing on request.");
        }

        if (string.IsNullOrEmpty(request.Text))
        {
            return SlackChatPostMessageResult.Skipped("text missing on request.");
        }

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(request.TeamId, ct)
            .ConfigureAwait(false);
        if (workspace is null || !workspace.Enabled)
        {
            return SlackChatPostMessageResult.MissingConfiguration(
                $"workspace '{request.TeamId}' is not registered or is disabled.");
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            return SlackChatPostMessageResult.MissingConfiguration(
                $"workspace '{request.TeamId}' has no bot-token secret reference.");
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
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to resolve bot-token secret '{SecretRef}' for workspace {TeamId} while posting root message correlation_id={CorrelationId}.",
                workspace.BotTokenSecretRef,
                request.TeamId,
                request.CorrelationId);
            return SlackChatPostMessageResult.MissingConfiguration(
                $"failed to resolve bot-token secret for workspace '{request.TeamId}'.");
        }

        if (string.IsNullOrEmpty(botToken))
        {
            return SlackChatPostMessageResult.MissingConfiguration(
                $"workspace '{request.TeamId}' bot-token secret resolved to empty.");
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, ChatPostMessageUrl)
        {
            Content = JsonContent.Create(BuildBody(request)),
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
                    "Slack chat.postMessage returned HTTP {StatusCode} for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                    (int)httpResponse.StatusCode,
                    request.TeamId,
                    request.ChannelId,
                    request.CorrelationId);
                return SlackChatPostMessageResult.NetworkFailure(
                    $"slack chat.postMessage returned HTTP {(int)httpResponse.StatusCode}.");
            }

            string responseBody = await httpResponse.Content
                .ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SlackChatPostMessageResult.NetworkFailure(
                    "slack chat.postMessage response was not a JSON object.");
            }

            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl)
                && okEl.ValueKind == JsonValueKind.True;

            if (ok)
            {
                string? ts = null;
                if (doc.RootElement.TryGetProperty("ts", out JsonElement tsEl)
                    && tsEl.ValueKind == JsonValueKind.String)
                {
                    ts = tsEl.GetString();
                }

                if (string.IsNullOrEmpty(ts))
                {
                    this.logger.LogWarning(
                        "Slack chat.postMessage returned ok=true with empty ts for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                        request.TeamId,
                        request.ChannelId,
                        request.CorrelationId);
                    return SlackChatPostMessageResult.NetworkFailure(
                        "slack chat.postMessage response was missing the message ts.");
                }

                string? channel = request.ChannelId;
                if (doc.RootElement.TryGetProperty("channel", out JsonElement channelEl)
                    && channelEl.ValueKind == JsonValueKind.String)
                {
                    string? echoed = channelEl.GetString();
                    if (!string.IsNullOrEmpty(echoed))
                    {
                        channel = echoed;
                    }
                }

                this.logger.LogInformation(
                    "Slack chat.postMessage created root message team_id={TeamId} channel_id={ChannelId} ts={Ts} correlation_id={CorrelationId}.",
                    request.TeamId,
                    channel,
                    ts,
                    request.CorrelationId);
                return SlackChatPostMessageResult.Success(ts!, channel);
            }

            string slackError = "unknown_error";
            if (doc.RootElement.TryGetProperty("error", out JsonElement errEl)
                && errEl.ValueKind == JsonValueKind.String)
            {
                slackError = errEl.GetString() ?? "unknown_error";
            }

            this.logger.LogWarning(
                "Slack chat.postMessage returned {{ok:false, error:'{SlackError}'}} for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                slackError,
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
            return SlackChatPostMessageResult.Failure(slackError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            this.logger.LogWarning(
                "Slack chat.postMessage timed out after {TimeoutMs} ms for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                this.requestTimeout.TotalMilliseconds,
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
            return SlackChatPostMessageResult.NetworkFailure(
                $"slack chat.postMessage timed out after {this.requestTimeout.TotalMilliseconds} ms.");
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.postMessage transport error for team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
            return SlackChatPostMessageResult.NetworkFailure(ex.Message);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.postMessage response body was malformed JSON for team_id={TeamId} correlation_id={CorrelationId}.",
                request.TeamId,
                request.CorrelationId);
            return SlackChatPostMessageResult.NetworkFailure(
                "slack chat.postMessage response body was malformed JSON.");
        }
        finally
        {
            httpResponse?.Dispose();
        }
    }

    /// <summary>
    /// Builds the JSON body for <c>chat.postMessage</c>. When the
    /// <see cref="SlackChatPostMessageRequest.ThreadTs"/> is non-null
    /// (Stage 6.2's <c>PostThreadedReplyAsync</c> path) the
    /// <c>thread_ts</c> field is included; otherwise the body is the
    /// minimal <c>{channel, text}</c> shape used for root messages.
    /// </summary>
    private static object BuildBody(SlackChatPostMessageRequest request)
    {
        if (string.IsNullOrEmpty(request.ThreadTs))
        {
            return new
            {
                channel = request.ChannelId,
                text = request.Text,
            };
        }

        return new
        {
            channel = request.ChannelId,
            thread_ts = request.ThreadTs,
            text = request.Text,
        };
    }
}

/// <summary>
/// Tunable knobs for <see cref="HttpClientSlackChatPostMessageClient"/>.
/// Bound through the standard <see cref="IOptions{TOptions}"/> pattern
/// from the <c>"Slack:ChatPostMessage"</c> configuration section.
/// </summary>
public sealed class SlackChatPostMessageClientOptions
{
    /// <summary>Configuration section name (<c>"Slack:ChatPostMessage"</c>).</summary>
    public const string SectionName = "Slack:ChatPostMessage";

    /// <summary>
    /// Default per-request timeout for <c>chat.postMessage</c> calls.
    /// </summary>
    /// <remarks>
    /// 10&#160;seconds. The thread manager runs on Stage 6.3's background
    /// dispatch loop where the 3-second Slack ACK budget does not apply,
    /// so a wider ceiling absorbs transient network variance without
    /// converting it into spurious dead-letters. Operators can tighten
    /// or loosen via configuration.
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-request timeout applied to outbound <c>chat.postMessage</c>
    /// calls. Non-positive values are coerced to
    /// <see cref="DefaultRequestTimeout"/> at consumption time so a
    /// misconfigured section cannot disable the timeout entirely.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;
}
