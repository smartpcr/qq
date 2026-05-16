// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackViewsOpenClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

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
/// Stage 4.1 default <see cref="ISlackViewsOpenClient"/>. Resolves the
/// per-workspace bot OAuth token via
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>
/// and POSTs to Slack's <c>views.open</c> Web API endpoint
/// (<c>https://slack.com/api/views.open</c>) over a shared
/// <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Architecture.md §2.15 names Stage 6.4's <c>SlackDirectApiClient</c>
/// as the production implementation (SlackNet-backed, sharing rate-limit
/// state with the outbound dispatcher). Stage 4.1 cannot wait for that
/// stage because the modal fast-path is what makes the
/// <c>/agent review</c> and <c>/agent escalate</c> ACCEPTANCE-criteria
/// work, so we ship this HttpClient-based implementation as the default
/// and let Stage 6.4 supersede it via
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// pre-registration.
/// </para>
/// <para>
/// The HTTP request applies a fixed 2.5-second timeout which is
/// deliberately tighter than Slack's 3-second ACK budget; that
/// leaves ~500 ms of headroom for the controller to write the
/// response and for the rest of the request pipeline to run.
/// </para>
/// </remarks>
internal sealed class HttpClientSlackViewsOpenClient : ISlackViewsOpenClient
{
    /// <summary>
    /// Public Slack endpoint for opening a view. Pinned by
    /// architecture.md and Slack's published Web API reference.
    /// </summary>
    public const string ViewsOpenUrl = "https://slack.com/api/views.open";

    /// <summary>
    /// Name of the typed <see cref="HttpClient"/> registered via
    /// <see cref="IHttpClientFactory"/>. Allows the host to layer
    /// resilience handlers (retry, circuit-breaker, telemetry) on
    /// top of the default registration without subclassing this
    /// client.
    /// </summary>
    public const string HttpClientName = "slack-views-open";

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMilliseconds(2500);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ILogger<HttpClientSlackViewsOpenClient> logger;
    private readonly TimeSpan requestTimeout;

    public HttpClientSlackViewsOpenClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackViewsOpenClient> logger)
        : this(httpClientFactory, workspaceStore, secretProvider, logger, DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a unit test override the
    /// request timeout.
    /// </summary>
    public HttpClientSlackViewsOpenClient(
        IHttpClientFactory httpClientFactory,
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ILogger<HttpClientSlackViewsOpenClient> logger,
        TimeSpan requestTimeout)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TeamId))
        {
            return SlackViewsOpenResult.MissingConfiguration("team_id missing on request.");
        }

        if (string.IsNullOrWhiteSpace(request.TriggerId))
        {
            return SlackViewsOpenResult.MissingConfiguration("trigger_id missing on request.");
        }

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(request.TeamId, ct)
            .ConfigureAwait(false);
        if (workspace is null || !workspace.Enabled)
        {
            return SlackViewsOpenResult.MissingConfiguration(
                $"workspace '{request.TeamId}' is not registered or is disabled.");
        }

        if (string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
        {
            return SlackViewsOpenResult.MissingConfiguration(
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
            // Honour caller cancellation -- never silently convert a
            // client-aborted request into a "missing configuration"
            // ephemeral error.
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to resolve bot-token secret '{SecretRef}' for workspace {TeamId} while opening modal view.",
                workspace.BotTokenSecretRef,
                request.TeamId);
            return SlackViewsOpenResult.MissingConfiguration(
                $"failed to resolve bot-token secret for workspace '{request.TeamId}'.");
        }

        if (string.IsNullOrEmpty(botToken))
        {
            return SlackViewsOpenResult.MissingConfiguration(
                $"workspace '{request.TeamId}' bot-token secret resolved to empty.");
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, ViewsOpenUrl)
        {
            Content = JsonContent.Create(new
            {
                trigger_id = request.TriggerId,
                view = request.ViewPayload,
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
                    "Slack views.open returned HTTP {StatusCode} for workspace {TeamId}.",
                    (int)httpResponse.StatusCode,
                    request.TeamId);
                return SlackViewsOpenResult.NetworkFailure(
                    $"slack views.open returned HTTP {(int)httpResponse.StatusCode}.");
            }

            string responseBody = await httpResponse.Content
                .ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SlackViewsOpenResult.NetworkFailure(
                    "slack views.open response was not a JSON object.");
            }

            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl)
                && okEl.ValueKind == JsonValueKind.True;

            if (ok)
            {
                return SlackViewsOpenResult.Success();
            }

            string slackError = "unknown_error";
            if (doc.RootElement.TryGetProperty("error", out JsonElement errEl)
                && errEl.ValueKind == JsonValueKind.String)
            {
                slackError = errEl.GetString() ?? "unknown_error";
            }

            this.logger.LogWarning(
                "Slack views.open returned {{ok:false, error:'{SlackError}'}} for workspace {TeamId}.",
                slackError,
                request.TeamId);
            return SlackViewsOpenResult.Failure(slackError);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            this.logger.LogWarning(
                "Slack views.open timed out after {TimeoutMs} ms for workspace {TeamId}.",
                this.requestTimeout.TotalMilliseconds,
                request.TeamId);
            return SlackViewsOpenResult.NetworkFailure(
                $"slack views.open timed out after {this.requestTimeout.TotalMilliseconds} ms.");
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack views.open transport error for workspace {TeamId}.",
                request.TeamId);
            return SlackViewsOpenResult.NetworkFailure(ex.Message);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack views.open response body was malformed JSON for workspace {TeamId}.",
                request.TeamId);
            return SlackViewsOpenResult.NetworkFailure("slack views.open response body was malformed JSON.");
        }
        finally
        {
            httpResponse?.Dispose();
        }
    }
}
