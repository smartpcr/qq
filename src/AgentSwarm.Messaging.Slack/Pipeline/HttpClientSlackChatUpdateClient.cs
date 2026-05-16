// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackChatUpdateClient.cs" company="Microsoft Corp.">
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
/// Default <see cref="ISlackChatUpdateClient"/> implementation: resolves
/// the per-workspace bot OAuth token via
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>
/// and POSTs to Slack's <c>chat.update</c> endpoint
/// (<c>https://slack.com/api/chat.update</c>) over a shared
/// <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Transport.HttpClientSlackViewsOpenClient"/> in
/// structure so the failure-mode handling stays consistent: missing
/// configuration is distinguished from Slack errors and from transport
/// failures, and all of them are returned (never thrown) so the
/// Stage 5.3 handler can write a single audit line without aborting
/// the surrounding decision-publish flow.
/// </para>
/// <para>
/// Stage 6.4's <c>SlackDirectApiClient</c> is expected to supersede
/// this implementation (sharing rate-limit state with the outbound
/// dispatcher); until then this client is what runs in production.
/// </para>
/// <para>
/// The per-request timeout is sourced from
/// <see cref="SlackChatUpdateClientOptions.RequestTimeout"/> so hosts
/// can tune it via the standard options pattern. The default
/// (<see cref="SlackChatUpdateClientOptions.DefaultRequestTimeout"/>,
/// 10&#160;seconds) deliberately exceeds Slack's 3-second interactive
/// ACK window because <c>chat.update</c> runs on the async handler
/// path AFTER the ACK has been flushed, so it is not bound by that
/// window and a tighter ceiling only manufactures spurious
/// dead-letters under normal network variance.
/// </para>
/// </remarks>
internal sealed class HttpClientSlackChatUpdateClient : ISlackChatUpdateClient
{
    /// <summary>Slack endpoint for <c>chat.update</c>.</summary>
    public const string ChatUpdateUrl = "https://slack.com/api/chat.update";

    /// <summary>Named <see cref="HttpClient"/> for resilience-handler layering.</summary>
    public const string HttpClientName = "slack-chat-update";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ILogger<HttpClientSlackChatUpdateClient> logger;
    private readonly TimeSpan requestTimeout;

    /// <summary>
    /// DI-friendly constructor. Resolves the request timeout from
    /// <see cref="IOptions{TOptions}"/> of
    /// <see cref="SlackChatUpdateClientOptions"/> so operators can
    /// override the default through configuration without recompiling.
    /// </summary>
    public HttpClientSlackChatUpdateClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackChatUpdateClient> logger,
        IOptions<SlackChatUpdateClientOptions> options)
        : this(
            httpClientFactory,
            workspaceStore,
            secretProvider,
            logger,
            (options ?? throw new ArgumentNullException(nameof(options))).Value?.RequestTimeout
                ?? SlackChatUpdateClientOptions.DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that pins the per-request timeout
    /// directly. Visible to the Slack test assembly via
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    internal HttpClientSlackChatUpdateClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackChatUpdateClient> logger,
        TimeSpan requestTimeout)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.requestTimeout = requestTimeout > TimeSpan.Zero
            ? requestTimeout
            : SlackChatUpdateClientOptions.DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task<SlackChatUpdateResult> UpdateAsync(SlackChatUpdateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TeamId))
        {
            return SlackChatUpdateResult.MissingConfiguration("team_id missing on request.");
        }

        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            return SlackChatUpdateResult.Skipped("channel_id missing on interactive payload.");
        }

        if (string.IsNullOrWhiteSpace(request.MessageTs))
        {
            return SlackChatUpdateResult.Skipped("message.ts missing on interactive payload.");
        }

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(request.TeamId, ct)
            .ConfigureAwait(false);
        if (workspace is null || !workspace.Enabled)
        {
            return SlackChatUpdateResult.MissingConfiguration(
                $"workspace '{request.TeamId}' is not registered or is disabled.");
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            return SlackChatUpdateResult.MissingConfiguration(
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
                "Failed to resolve bot-token secret '{SecretRef}' for workspace {TeamId} while updating message.",
                workspace.BotTokenSecretRef,
                request.TeamId);
            return SlackChatUpdateResult.MissingConfiguration(
                $"failed to resolve bot-token secret for workspace '{request.TeamId}'.");
        }

        if (string.IsNullOrEmpty(botToken))
        {
            return SlackChatUpdateResult.MissingConfiguration(
                $"workspace '{request.TeamId}' bot-token secret resolved to empty.");
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, ChatUpdateUrl)
        {
            Content = JsonContent.Create(new
            {
                channel = request.ChannelId,
                ts = request.MessageTs,
                text = request.Text,
                blocks = request.Blocks,
            }),
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
                    "Slack chat.update returned HTTP {StatusCode} for workspace {TeamId} channel {ChannelId} ts {MessageTs}.",
                    (int)httpResponse.StatusCode,
                    request.TeamId,
                    request.ChannelId,
                    request.MessageTs);
                return SlackChatUpdateResult.NetworkFailure(
                    $"slack chat.update returned HTTP {(int)httpResponse.StatusCode}.");
            }

            string responseBody = await httpResponse.Content
                .ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SlackChatUpdateResult.NetworkFailure(
                    "slack chat.update response was not a JSON object.");
            }

            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl)
                && okEl.ValueKind == JsonValueKind.True;
            if (ok)
            {
                return SlackChatUpdateResult.Success();
            }

            string slackError = "unknown_error";
            if (doc.RootElement.TryGetProperty("error", out JsonElement errEl)
                && errEl.ValueKind == JsonValueKind.String)
            {
                slackError = errEl.GetString() ?? "unknown_error";
            }

            this.logger.LogWarning(
                "Slack chat.update returned {{ok:false, error:'{SlackError}'}} for workspace {TeamId} channel {ChannelId} ts {MessageTs}.",
                slackError,
                request.TeamId,
                request.ChannelId,
                request.MessageTs);
            return SlackChatUpdateResult.Failure(slackError);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            this.logger.LogWarning(
                "Slack chat.update timed out after {TimeoutMs} ms for workspace {TeamId} channel {ChannelId} ts {MessageTs}.",
                this.requestTimeout.TotalMilliseconds,
                request.TeamId,
                request.ChannelId,
                request.MessageTs);
            return SlackChatUpdateResult.NetworkFailure(
                $"slack chat.update timed out after {this.requestTimeout.TotalMilliseconds} ms.");
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.update transport error for workspace {TeamId} channel {ChannelId} ts {MessageTs}.",
                request.TeamId,
                request.ChannelId,
                request.MessageTs);
            return SlackChatUpdateResult.NetworkFailure(ex.Message);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack chat.update response body was malformed JSON for workspace {TeamId} channel {ChannelId} ts {MessageTs}.",
                request.TeamId,
                request.ChannelId,
                request.MessageTs);
            return SlackChatUpdateResult.NetworkFailure("slack chat.update response body was malformed JSON.");
        }
        finally
        {
            httpResponse?.Dispose();
        }
    }
}

/// <summary>
/// Tunable knobs for <see cref="HttpClientSlackChatUpdateClient"/>.
/// Bound through the standard <see cref="IOptions{TOptions}"/> pattern;
/// when no <c>Configure</c> / <c>Bind</c> call is registered the
/// defaults below are used.
/// </summary>
/// <remarks>
/// <para>
/// Exposed publicly so production composition roots can override the
/// per-request timeout from configuration. The matching
/// <c>SlackInteractionDispatchServiceCollectionExtensions</c> wiring
/// resolves <see cref="IOptions{TOptions}"/> automatically; hosts that
/// do not register a binding simply pick up
/// <see cref="DefaultRequestTimeout"/>.
/// </para>
/// </remarks>
public sealed class SlackChatUpdateClientOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack:ChatUpdate"</c>) the
    /// options bind from. Exposed as a constant so the extension
    /// method and consumers can agree without duplicating the literal.
    /// </summary>
    public const string SectionName = "Slack:ChatUpdate";

    /// <summary>
    /// Default per-request timeout for <c>chat.update</c> calls.
    /// </summary>
    /// <remarks>
    /// 10&#160;seconds: <c>chat.update</c> executes on the async handler
    /// path AFTER the interactive HTTP ACK has been flushed, so the
    /// 3-second Slack ACK budget does not apply. A 10-second ceiling
    /// keeps the worst-case slot bounded while comfortably absorbing
    /// transient network variance.
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-request timeout applied to the outbound <c>chat.update</c>
    /// HTTP call. Non-positive values are coerced to
    /// <see cref="DefaultRequestTimeout"/> at consumption time so a
    /// misconfigured section cannot disable the timeout entirely.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;
}
