// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackOutboundDispatchClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 6.3 unit tests for the
/// <see cref="HttpClientSlackOutboundDispatchClient"/>. Drives the
/// dispatch through a stub <see cref="HttpClient"/> +
/// <see cref="IHttpClientFactory"/> so the endpoint URL, JSON
/// payload, and classified outcome (success / 429 + Retry-After /
/// transient / permanent / missing-configuration) can be pinned
/// without touching Slack.
/// </summary>
public sealed class HttpClientSlackOutboundDispatchClientTests
{
    private const string TeamId = "T-OUT";
    private const string ChannelId = "C-OUT";
    private const string ThreadTs = "1700000000.000001";
    private const string SecretRef = "test://bot-token/T-OUT";
    private const string BotToken = "xoxb-out";

    [Fact]
    public async Task PostMessage_dispatches_to_chat_postMessage_with_thread_ts()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(
                HttpStatusCode.OK,
                "{\"ok\":true,\"channel\":\"C-OUT\",\"ts\":\"1700000050.000010\"}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.Success);
        result.MessageTs.Should().Be("1700000050.000010");
        result.HttpStatusCode.Should().Be((int)HttpStatusCode.OK);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be(HttpClientSlackOutboundDispatchClient.ChatPostMessageUrl);
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(BotToken);
        handler.LastRequestBody.Should().Contain($"\"channel\":\"{ChannelId}\"");
        handler.LastRequestBody.Should().Contain($"\"thread_ts\":\"{ThreadTs}\"");
    }

    [Fact]
    public async Task PostMessage_without_thread_ts_omits_thread_ts_field()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(
                HttpStatusCode.OK,
                "{\"ok\":true,\"ts\":\"1700000099.000001\"}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs: null,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        handler.LastRequestBody.Should().NotContain("\"thread_ts\"");
        handler.LastRequestBody.Should().Contain($"\"channel\":\"{ChannelId}\"");
    }

    [Fact]
    public async Task UpdateMessage_dispatches_to_chat_update_with_ts()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.UpdateMessage,
                TeamId,
                ChannelId,
                ThreadTs: ThreadTs,
                MessageTs: "1700000111.000222",
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.Success);
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be(HttpClientSlackOutboundDispatchClient.ChatUpdateUrl);
        handler.LastRequestBody.Should().Contain("\"ts\":\"1700000111.000222\"");
        handler.LastRequestBody.Should().Contain($"\"channel\":\"{ChannelId}\"");
    }

    [Fact]
    public async Task ViewsUpdate_dispatches_to_views_update_with_view_id_wrapper()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.ViewsUpdate,
                TeamId,
                ChannelId: string.Empty,
                ThreadTs: null,
                MessageTs: null,
                ViewId: "V-1",
                BlockKitPayload: "{\"type\":\"modal\",\"title\":{\"type\":\"plain_text\",\"text\":\"x\"}}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.Success);
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be(HttpClientSlackOutboundDispatchClient.ViewsUpdateUrl);
        handler.LastRequestBody.Should().Contain("\"view_id\":\"V-1\"");
        handler.LastRequestBody.Should().Contain("\"view\":");
    }

    [Fact]
    public async Task Returns_RateLimited_on_http_429_with_Retry_After_seconds()
    {
        StubHttpMessageHandler handler = new(
            (_, _) =>
            {
                HttpResponseMessage resp = CreateResponse(HttpStatusCode.TooManyRequests, "rate_limited");
                resp.Headers.Add("Retry-After", "7");
                return resp;
            });
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.RateLimited);
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().Be(TimeSpan.FromSeconds(7));
        result.HttpStatusCode.Should().Be(429);
    }

    [Fact]
    public async Task Returns_RateLimited_with_clamp_when_Retry_After_huge()
    {
        StubHttpMessageHandler handler = new(
            (_, _) =>
            {
                HttpResponseMessage resp = CreateResponse(HttpStatusCode.TooManyRequests, "rate_limited");
                resp.Headers.Add("Retry-After", "999999");
                return resp;
            });
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.RateLimited);
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5),
            "absurdly long Retry-After values MUST be clamped to keep the dispatcher loop responsive");
    }

    [Fact]
    public async Task Returns_RateLimited_with_fallback_when_Retry_After_missing()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.TooManyRequests, "rate_limited"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.RateLimited);
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.Zero,
            "missing Retry-After must still produce a non-zero fallback so the dispatcher pauses the bucket");
    }

    [Fact]
    public async Task Returns_TransientFailure_on_http_500()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.InternalServerError, "boom"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.TransientFailure);
        result.HttpStatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Returns_PermanentFailure_on_http_400()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.BadRequest, "bad"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.PermanentFailure);
        result.HttpStatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Returns_PermanentFailure_on_slack_ok_false_with_unrecoverable_error()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"channel_not_found\"}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.PermanentFailure);
        result.SlackError.Should().Be("channel_not_found");
    }

    [Fact]
    public async Task Returns_TransientFailure_on_slack_ok_false_with_known_transient_error()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"service_unavailable\"}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.TransientFailure);
        result.SlackError.Should().Be("service_unavailable");
    }

    [Fact]
    public async Task Returns_RateLimited_on_slack_ok_false_ratelimited()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"ratelimited\"}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.RateLimited);
        result.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Returns_TransientFailure_on_malformed_response_body()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "<<not json>>"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.TransientFailure);
        result.SlackError.Should().Be("malformed_response");
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_team_id_blank()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId: string.Empty,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.MissingConfiguration);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_workspace_unknown()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(
            handler,
            workspaces: new Dictionary<string, SlackWorkspaceConfig>());

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.MissingConfiguration);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_channel_blank_on_post_message()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        SlackOutboundDispatchResult result = await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId: string.Empty,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(SlackOutboundDispatchOutcome.MissingConfiguration);
    }

    [Fact]
    public async Task Cancellation_propagates_OperationCanceledException()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        HttpClientSlackOutboundDispatchClient client = BuildClient(handler);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await client.DispatchAsync(
            new SlackOutboundDispatchRequest(
                SlackOutboundOperationKind.PostMessage,
                TeamId,
                ChannelId,
                ThreadTs,
                MessageTs: null,
                ViewId: null,
                BlockKitPayload: "{\"blocks\":[]}",
                CorrelationId: "corr-1"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpClientSlackOutboundDispatchClient BuildClient(
        StubHttpMessageHandler handler,
        IReadOnlyDictionary<string, SlackWorkspaceConfig>? workspaces = null,
        ISecretProvider? secretsOverride = null)
    {
        StubHttpClientFactory factory = new(handler);
        StubWorkspaceConfigStore store = new(workspaces ?? new Dictionary<string, SlackWorkspaceConfig>
        {
            [TeamId] = new SlackWorkspaceConfig
            {
                TeamId = TeamId,
                BotTokenSecretRef = SecretRef,
                Enabled = true,
            },
        });

        ISecretProvider secrets;
        if (secretsOverride is not null)
        {
            secrets = secretsOverride;
        }
        else
        {
            StubSecretProvider stub = new();
            stub.Set(SecretRef, BotToken);
            secrets = stub;
        }

        return new HttpClientSlackOutboundDispatchClient(
            factory,
            store,
            secrets,
            NullLogger<HttpClientSlackOutboundDispatchClient>.Instance,
            TimeProvider.System,
            TimeSpan.FromSeconds(5));
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;

        public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            if (request.Content is not null)
            {
                this.LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return this.responder(request, cancellationToken);
        }
    }

    private sealed class StubWorkspaceConfigStore : ISlackWorkspaceConfigStore
    {
        private readonly IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces;

        public StubWorkspaceConfigStore(IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces)
        {
            this.workspaces = workspaces;
        }

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(teamId) && this.workspaces.TryGetValue(teamId, out SlackWorkspaceConfig? cfg))
            {
                return Task.FromResult<SlackWorkspaceConfig?>(cfg);
            }

            return Task.FromResult<SlackWorkspaceConfig?>(null);
        }

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(
                new List<SlackWorkspaceConfig>(this.workspaces.Values));
        }
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public void Set(string secretRef, string value) => this.values[secretRef] = value;

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            if (this.values.TryGetValue(secretRef, out string? value))
            {
                return Task.FromResult(value);
            }

            throw new SecretNotFoundException(secretRef);
        }
    }
}
