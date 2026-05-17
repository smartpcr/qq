// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackOutboundDispatchClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackOutboundDispatchClient"/>: resolves the
/// per-workspace bot OAuth token via
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>,
/// merges the channel / thread / message references into the
/// pre-rendered Block Kit JSON, and POSTs to the matching Slack Web
/// API endpoint (<c>chat.postMessage</c>, <c>chat.update</c>, or
/// <c>views.update</c>).
/// </summary>
/// <remarks>
/// <para>
/// Stage 6.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// steps 1, 6. The class is intentionally modelled on
/// <see cref="HttpClientSlackChatUpdateClient"/> + the
/// <see cref="HttpClientSlackChatPostMessageClient"/>: a single
/// shared <see cref="HttpClient"/> through
/// <see cref="IHttpClientFactory"/>, classified result-returning
/// failure modes, and a request timeout sourced from
/// <see cref="SlackOutboundDispatchClientOptions"/>.
/// </para>
/// <para>
/// HTTP 429 handling is explicit: the response's <c>Retry-After</c>
/// header (RFC 7231 -- either delta-seconds or HTTP-date) is parsed,
/// clamped to a sensible upper bound, and surfaced on
/// <see cref="SlackOutboundDispatchResult.RetryAfter"/> so the
/// dispatcher can hand it to <see cref="ISlackRateLimiter.NotifyRetryAfter"/>.
/// </para>
/// </remarks>
internal sealed class HttpClientSlackOutboundDispatchClient : ISlackOutboundDispatchClient
{
    /// <summary>Slack endpoint for <c>chat.postMessage</c>.</summary>
    public const string ChatPostMessageUrl = "https://slack.com/api/chat.postMessage";

    /// <summary>Slack endpoint for <c>chat.update</c>.</summary>
    public const string ChatUpdateUrl = "https://slack.com/api/chat.update";

    /// <summary>Slack endpoint for <c>views.update</c>.</summary>
    public const string ViewsUpdateUrl = "https://slack.com/api/views.update";

    /// <summary>Named <see cref="HttpClient"/> for resilience-handler layering.</summary>
    public const string HttpClientName = "slack-outbound-dispatch";

    /// <summary>
    /// Slack error strings considered transient (the dispatcher will
    /// retry) per the Slack Web API rate-limit / retry guidance.
    /// </summary>
    private static readonly HashSet<string> TransientSlackErrors = new(StringComparer.Ordinal)
    {
        "service_unavailable",
        "internal_error",
        "team_added_to_org",
        "request_timeout",
        "fatal_error",
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ILogger<HttpClientSlackOutboundDispatchClient> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan requestTimeout;

    /// <summary>
    /// DI-friendly constructor.
    /// </summary>
    public HttpClientSlackOutboundDispatchClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackOutboundDispatchClient> logger,
        IOptions<SlackOutboundDispatchClientOptions> options)
        : this(
            httpClientFactory,
            workspaceStore,
            secretProvider,
            logger,
            TimeProvider.System,
            (options ?? throw new ArgumentNullException(nameof(options))).Value?.RequestTimeout
                ?? SlackOutboundDispatchClientOptions.DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that pins the per-request timeout and
    /// the clock directly.
    /// </summary>
    internal HttpClientSlackOutboundDispatchClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackOutboundDispatchClient> logger,
        TimeProvider timeProvider,
        TimeSpan requestTimeout)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.requestTimeout = requestTimeout > TimeSpan.Zero
            ? requestTimeout
            : SlackOutboundDispatchClientOptions.DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task<SlackOutboundDispatchResult> DispatchAsync(
        SlackOutboundDispatchRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.TeamId))
        {
            return SlackOutboundDispatchResult.MissingConfiguration("team_id missing on request.");
        }

        if (string.IsNullOrEmpty(request.BlockKitPayload))
        {
            return SlackOutboundDispatchResult.MissingConfiguration("block_kit payload missing on request.");
        }

        if (request.Operation == SlackOutboundOperationKind.PostMessage
            || request.Operation == SlackOutboundOperationKind.UpdateMessage)
        {
            if (string.IsNullOrWhiteSpace(request.ChannelId))
            {
                return SlackOutboundDispatchResult.MissingConfiguration("channel_id missing on request.");
            }
        }

        if (request.Operation == SlackOutboundOperationKind.UpdateMessage
            && string.IsNullOrWhiteSpace(request.MessageTs))
        {
            return SlackOutboundDispatchResult.MissingConfiguration("message_ts missing on chat.update request.");
        }

        if (request.Operation == SlackOutboundOperationKind.ViewsUpdate
            && string.IsNullOrWhiteSpace(request.ViewId))
        {
            return SlackOutboundDispatchResult.MissingConfiguration("view_id missing on views.update request.");
        }

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(request.TeamId, ct)
            .ConfigureAwait(false);
        if (workspace is null || !workspace.Enabled)
        {
            return SlackOutboundDispatchResult.MissingConfiguration(
                $"workspace '{request.TeamId}' is not registered or is disabled.");
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            return SlackOutboundDispatchResult.MissingConfiguration(
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
                "Slack outbound dispatch failed to resolve bot-token secret '{SecretRef}' for workspace {TeamId} correlation_id={CorrelationId}.",
                workspace.BotTokenSecretRef,
                request.TeamId,
                request.CorrelationId);
            return SlackOutboundDispatchResult.MissingConfiguration(
                $"failed to resolve bot-token secret for workspace '{request.TeamId}'.");
        }

        if (string.IsNullOrEmpty(botToken))
        {
            return SlackOutboundDispatchResult.MissingConfiguration(
                $"workspace '{request.TeamId}' bot-token secret resolved to empty.");
        }

        string url = ResolveEndpoint(request.Operation);
        string body = this.BuildBody(request);

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using CancellationTokenSource timeoutCts = new(this.requestTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpResponseMessage? httpResponse = null;
        try
        {
            HttpClient httpClient = this.httpClientFactory.CreateClient(HttpClientName);
            httpResponse = await httpClient.SendAsync(httpRequest, linked.Token).ConfigureAwait(false);

            string responseBody = await httpResponse.Content
                .ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            if ((int)httpResponse.StatusCode == 429)
            {
                TimeSpan retryAfter = this.ParseRetryAfter(httpResponse);
                this.logger.LogWarning(
                    "Slack outbound dispatch hit HTTP 429 for op={Operation} team_id={TeamId} channel_id={ChannelId} retry_after_ms={RetryAfterMs} correlation_id={CorrelationId}.",
                    request.Operation,
                    request.TeamId,
                    request.ChannelId,
                    retryAfter.TotalMilliseconds,
                    request.CorrelationId);
                return SlackOutboundDispatchResult.RateLimited((int)httpResponse.StatusCode, retryAfter, responseBody);
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                int code = (int)httpResponse.StatusCode;
                bool transient = code >= 500 && code <= 599;
                string err = $"http_{code}";
                this.logger.LogWarning(
                    "Slack outbound dispatch returned HTTP {StatusCode} for op={Operation} team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                    code,
                    request.Operation,
                    request.TeamId,
                    request.ChannelId,
                    request.CorrelationId);
                return transient
                    ? SlackOutboundDispatchResult.Transient(code, err, responseBody)
                    : SlackOutboundDispatchResult.Permanent(code, err, responseBody);
            }

            // HTTP 200; inspect Slack {ok: true/false}.
            string? slackError = null;
            string? messageTs = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl)
                        && okEl.ValueKind == JsonValueKind.True;

                    if (ok)
                    {
                        if (doc.RootElement.TryGetProperty("ts", out JsonElement tsEl)
                            && tsEl.ValueKind == JsonValueKind.String)
                        {
                            messageTs = tsEl.GetString();
                        }

                        this.logger.LogInformation(
                            "Slack outbound dispatch succeeded op={Operation} team_id={TeamId} channel_id={ChannelId} ts={Ts} correlation_id={CorrelationId}.",
                            request.Operation,
                            request.TeamId,
                            request.ChannelId,
                            messageTs,
                            request.CorrelationId);
                        return SlackOutboundDispatchResult.Success(
                            (int)httpResponse.StatusCode, messageTs, responseBody);
                    }

                    if (doc.RootElement.TryGetProperty("error", out JsonElement errEl)
                        && errEl.ValueKind == JsonValueKind.String)
                    {
                        slackError = errEl.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                this.logger.LogWarning(
                    "Slack outbound dispatch response body was malformed JSON op={Operation} team_id={TeamId} correlation_id={CorrelationId}.",
                    request.Operation,
                    request.TeamId,
                    request.CorrelationId);
                return SlackOutboundDispatchResult.Transient(
                    (int)httpResponse.StatusCode, "malformed_response", responseBody);
            }

            slackError ??= "unknown_error";
            bool slackTransient = TransientSlackErrors.Contains(slackError)
                || string.Equals(slackError, "ratelimited", StringComparison.Ordinal);

            // "ratelimited" surfaced inside the JSON body (rather than an
            // HTTP 429) is rare but documented; map it to RateLimited so
            // the dispatcher pauses correctly even when Slack's edge did
            // not raise the status code.
            if (string.Equals(slackError, "ratelimited", StringComparison.Ordinal))
            {
                TimeSpan retryAfter = this.ParseRetryAfter(httpResponse);
                return SlackOutboundDispatchResult.RateLimited(
                    (int)httpResponse.StatusCode, retryAfter, responseBody);
            }

            this.logger.LogWarning(
                "Slack outbound dispatch returned {{ok:false, error:'{SlackError}'}} op={Operation} team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                slackError,
                request.Operation,
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);

            return slackTransient
                ? SlackOutboundDispatchResult.Transient((int)httpResponse.StatusCode, slackError, responseBody)
                : SlackOutboundDispatchResult.Permanent((int)httpResponse.StatusCode, slackError, responseBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            this.logger.LogWarning(
                "Slack outbound dispatch timed out after {TimeoutMs} ms op={Operation} team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                this.requestTimeout.TotalMilliseconds,
                request.Operation,
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
            return SlackOutboundDispatchResult.Transient(0, "timeout", null);
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack outbound dispatch transport error op={Operation} team_id={TeamId} channel_id={ChannelId} correlation_id={CorrelationId}.",
                request.Operation,
                request.TeamId,
                request.ChannelId,
                request.CorrelationId);
            return SlackOutboundDispatchResult.Transient(0, ex.Message, null);
        }
        finally
        {
            httpResponse?.Dispose();
        }
    }

    private static string ResolveEndpoint(SlackOutboundOperationKind operation) => operation switch
    {
        SlackOutboundOperationKind.PostMessage => ChatPostMessageUrl,
        SlackOutboundOperationKind.UpdateMessage => ChatUpdateUrl,
        SlackOutboundOperationKind.ViewsUpdate => ViewsUpdateUrl,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation),
            operation,
            $"Unsupported Slack outbound operation '{operation}'."),
    };

    /// <summary>
    /// Builds the JSON body for the outbound call. The pre-rendered
    /// Block Kit payload may be either the FULL message body (already
    /// containing <c>blocks</c> / <c>attachments</c>) or a raw blocks
    /// array; the dispatcher merges the channel / thread / message
    /// references into the payload before POSTing.
    /// </summary>
    private string BuildBody(SlackOutboundDispatchRequest request)
    {
        // Parse the rendered payload into a JsonNode so we can merge
        // additional fields without reflectively building a giant
        // anonymous object hierarchy. Falling back to a raw "blocks"
        // wrapper when the payload is not an object keeps the seam
        // friendly to test fixtures that supply a stub blocks array.
        JsonNode root;
        try
        {
            root = JsonNode.Parse(request.BlockKitPayload) ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Treat the payload as opaque plain text -- preserves
            // deliverability even when the renderer hands us something
            // the parser cannot understand.
            root = new JsonObject { ["text"] = request.BlockKitPayload };
        }

        JsonObject body = root is JsonObject obj ? obj : new JsonObject { ["blocks"] = root };

        switch (request.Operation)
        {
            case SlackOutboundOperationKind.PostMessage:
                body["channel"] = request.ChannelId;
                if (!string.IsNullOrEmpty(request.ThreadTs))
                {
                    body["thread_ts"] = request.ThreadTs;
                }

                break;

            case SlackOutboundOperationKind.UpdateMessage:
                body["channel"] = request.ChannelId;
                body["ts"] = request.MessageTs;
                break;

            case SlackOutboundOperationKind.ViewsUpdate:
                // views.update expects {"view_id": "...", "view": {...}}
                // -- when the renderer hands us a bare view object, wrap
                // it; otherwise assume the payload already carries the
                // outer envelope.
                if (!body.ContainsKey("view_id"))
                {
                    JsonObject wrapped = new()
                    {
                        ["view_id"] = request.ViewId,
                        ["view"] = body,
                    };
                    body = wrapped;
                }
                else
                {
                    body["view_id"] = request.ViewId;
                }

                break;
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// Parses the response's <c>Retry-After</c> header (RFC 7231 --
    /// either delta-seconds or HTTP-date). Falls back to a 1-second
    /// floor when absent or unparseable so the dispatcher always
    /// applies SOME pause on a 429.
    /// </summary>
    private TimeSpan ParseRetryAfter(HttpResponseMessage response)
    {
        const int MaxRetryAfterSeconds = 300; // five minutes -- harsh upper bound on a single pause.
        const int FallbackSeconds = 1;

        if (response.Headers.RetryAfter is RetryConditionHeaderValue h)
        {
            if (h.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return ClampRetryAfter(delta);
            }

            if (h.Date is DateTimeOffset deadline)
            {
                TimeSpan d = deadline - this.timeProvider.GetUtcNow();
                if (d > TimeSpan.Zero)
                {
                    return ClampRetryAfter(d);
                }
            }
        }

        if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
        {
            foreach (string raw in values)
            {
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                    && seconds > 0)
                {
                    return ClampRetryAfter(TimeSpan.FromSeconds(seconds));
                }
            }
        }

        return TimeSpan.FromSeconds(FallbackSeconds);

        static TimeSpan ClampRetryAfter(TimeSpan candidate)
        {
            TimeSpan max = TimeSpan.FromSeconds(MaxRetryAfterSeconds);
            return candidate > max ? max : candidate;
        }
    }
}

/// <summary>
/// Tunable knobs for <see cref="HttpClientSlackOutboundDispatchClient"/>.
/// Bound through the standard <see cref="IOptions{TOptions}"/> pattern
/// from <c>"Slack:Outbound"</c>.
/// </summary>
public sealed class SlackOutboundDispatchClientOptions
{
    /// <summary>Configuration section name (<c>"Slack:Outbound"</c>).</summary>
    public const string SectionName = "Slack:Outbound";

    /// <summary>
    /// Default per-request timeout for outbound calls.
    /// </summary>
    /// <remarks>
    /// 10&#160;seconds: the dispatcher runs on a background loop where
    /// the 3-second Slack interactive ACK budget does not apply, so a
    /// wider ceiling absorbs transient network variance without
    /// converting it into spurious dead-letters.
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-request timeout applied to every outbound HTTP call.
    /// Non-positive values fall back to <see cref="DefaultRequestTimeout"/>.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;
}
