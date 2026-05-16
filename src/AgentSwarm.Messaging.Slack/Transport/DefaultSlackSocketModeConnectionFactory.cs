// -----------------------------------------------------------------------
// <copyright file="DefaultSlackSocketModeConnectionFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Production <see cref="ISlackSocketModeConnectionFactory"/>. Calls
/// <c>https://slack.com/api/apps.connections.open</c> with the workspace's
/// app-level token to retrieve a fresh WSS URL, then opens a
/// <see cref="ClientWebSocket"/> against that URL.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The factory accepts an <see cref="IHttpClientFactory"/> instead of a
/// raw <see cref="HttpClient"/> so the composition root can layer
/// resilience handlers (retry, circuit-breaker) on the
/// <see cref="HttpClientName"/> named client without subclassing this
/// type.
/// </para>
/// </remarks>
internal sealed class DefaultSlackSocketModeConnectionFactory : ISlackSocketModeConnectionFactory
{
    /// <summary>
    /// Named <see cref="HttpClient"/> registration used by this factory to
    /// call <c>apps.connections.open</c>. Exposed so the composition root
    /// can attach resilience handlers via
    /// <see cref="Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions.AddHttpClient(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>.
    /// </summary>
    public const string HttpClientName = "AgentSwarm.Slack.SocketMode.AppsConnectionsOpen";

    /// <summary>
    /// Slack endpoint that issues a short-lived WSS URL bound to the
    /// supplied app-level token.
    /// </summary>
    public const string AppsConnectionsOpenUrl = "https://slack.com/api/apps.connections.open";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly SlackSocketModeOptions options;

    public DefaultSlackSocketModeConnectionFactory(
        IHttpClientFactory httpClientFactory,
        SlackSocketModeOptions options)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<ISlackSocketModeConnection> ConnectAsync(string appLevelToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appLevelToken))
        {
            throw new ArgumentException("App-level token must be non-empty.", nameof(appLevelToken));
        }

        string wssUrl = await this.OpenSlackSocketAsync(appLevelToken, ct).ConfigureAwait(false);

        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(new Uri(wssUrl), ct).ConfigureAwait(false);
            return new ClientWebSocketSlackSocketModeConnection(socket, this.options.ReceiveBufferSize);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<string> OpenSlackSocketAsync(string appLevelToken, CancellationToken ct)
    {
        HttpClient client = this.httpClientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Post, AppsConnectionsOpenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appLevelToken);
        request.Content = new StringContent(string.Empty);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool ok = root.TryGetProperty("ok", out JsonElement okElt)
            && okElt.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            string? error = root.TryGetProperty("error", out JsonElement errElt)
                && errElt.ValueKind == JsonValueKind.String
                ? errElt.GetString()
                : null;
            throw new InvalidOperationException(
                $"Slack apps.connections.open failed: ok=false error={error ?? "unknown"}.");
        }

        string? url = root.TryGetProperty("url", out JsonElement urlElt)
            && urlElt.ValueKind == JsonValueKind.String
            ? urlElt.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "Slack apps.connections.open returned ok=true but no url field; cannot open Socket Mode WebSocket.");
        }

        return url!;
    }
}
