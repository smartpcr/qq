// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackChatPostMessageClientTests.cs" company="Microsoft Corp.">
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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 6.2 unit tests for the production
/// <see cref="HttpClientSlackChatPostMessageClient"/>. Drives the
/// request through a stub <see cref="HttpClient"/> +
/// <see cref="IHttpClientFactory"/> so the URL, Authorization header,
/// JSON body, and the structured result (ok + ts; slack error;
/// transport error; missing-configuration) can be pinned without
/// touching Slack. Mirrors the test pattern established for
/// <see cref="HttpClientSlackThreadedReplyPosterTests"/>.
/// </summary>
public sealed class HttpClientSlackChatPostMessageClientTests
{
    private const string TeamId = "T01TEAM";
    private const string ChannelId = "C01CHAN";
    private const string SecretRef = "test://bot-token/T01TEAM";
    private const string BotToken = "xoxb-test-bot-token";

    [Fact]
    public async Task Posts_to_chat_postMessage_with_bearer_token_channel_and_text_and_returns_ts()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(
                HttpStatusCode.OK,
                "{\"ok\":true,\"channel\":\"C01CHAN\",\"ts\":\"1700000200.000300\"}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "Task TASK-1 started.", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be(HttpClientSlackChatPostMessageClient.ChatPostMessageUrl);
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(BotToken);

        handler.LastRequestBody.Should().Contain($"\"channel\":\"{ChannelId}\"");
        handler.LastRequestBody.Should().Contain("\"text\":\"Task TASK-1 started.\"");
        handler.LastRequestBody.Should().NotContain("\"thread_ts\"",
            "root-message creation (ThreadTs == null) is intentionally a top-level post so Slack returns a new thread anchor");

        result.IsSuccess.Should().BeTrue();
        result.Ts.Should().Be("1700000200.000300");
        result.Channel.Should().Be("C01CHAN");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Includes_thread_ts_when_request_has_ThreadTs_set()
    {
        // Stage 6.2's PostThreadedReplyAsync path: when the request
        // carries a non-null ThreadTs the JSON body MUST surface the
        // thread_ts field verbatim so Slack threads the reply under
        // the owning root.
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(
                HttpStatusCode.OK,
                "{\"ok\":true,\"channel\":\"C01CHAN\",\"ts\":\"1700000200.000999\"}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "Reply text", "corr-1", ThreadTs: "1700000200.000300"),
            CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"thread_ts\":\"1700000200.000300\"");
        handler.LastRequestBody.Should().Contain($"\"channel\":\"{ChannelId}\"");
        handler.LastRequestBody.Should().Contain("\"text\":\"Reply text\"");
        result.IsSuccess.Should().BeTrue();
        result.Ts.Should().Be("1700000200.000999");
    }

    [Fact]
    public async Task Returns_SlackError_with_error_string_when_ok_false()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"channel_not_found\"}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(SlackChatPostMessageResultKind.SlackError);
        result.Error.Should().Be("channel_not_found");
        result.IsChannelMissing.Should().BeTrue(
            "channel_not_found is a recovery trigger for the thread manager fallback path");
    }

    [Theory]
    [InlineData("channel_not_found")]
    [InlineData("is_archived")]
    [InlineData("not_in_channel")]
    [InlineData("message_not_found")]
    [InlineData("thread_not_found")]
    public async Task IsChannelMissing_recognises_known_recovery_error_codes(string error)
    {
        SlackChatPostMessageResult result = SlackChatPostMessageResult.Failure(error);
        result.IsChannelMissing.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Returns_NetworkFailure_on_non_success_http_status()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.InternalServerError, "{}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.NetworkFailure);
        result.Ts.Should().BeNull();
    }

    [Fact]
    public async Task Returns_NetworkFailure_when_ok_true_but_ts_missing()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.NetworkFailure,
            "an ok:true response without ts cannot be persisted as a thread anchor");
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_team_unregistered()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(
            handler,
            workspaces: new Dictionary<string, SlackWorkspaceConfig>());

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull(
            "an unregistered workspace MUST NOT trigger an unauthenticated chat.postMessage call");
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_workspace_disabled()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(
            handler,
            workspaces: new Dictionary<string, SlackWorkspaceConfig>
            {
                [TeamId] = new SlackWorkspaceConfig
                {
                    TeamId = TeamId,
                    BotTokenSecretRef = SecretRef,
                    Enabled = false,
                },
            });

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_bot_token_secret_unresolvable()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        StubSecretProvider secrets = new();
        ISlackChatPostMessageClient client = BuildClient(handler, secretsOverride: secrets);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull(
            "an unresolvable bot-token secret MUST NOT result in an unauthenticated chat.postMessage call");
    }

    [Fact]
    public async Task Returns_MissingConfiguration_when_bot_token_resolves_empty()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        StubSecretProvider secrets = new();
        secrets.Set(SecretRef, string.Empty);
        ISlackChatPostMessageClient client = BuildClient(handler, secretsOverride: secrets);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Skipped_when_team_id_blank()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId: string.Empty, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Skipped_when_channel_id_blank()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId: string.Empty, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.Skipped);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Skipped_when_text_empty()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, Text: string.Empty, "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.Skipped);
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Caller_cancelled_token_propagates_OperationCanceledException()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_NetworkFailure_when_response_body_is_not_json()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "<<not json>>"));
        ISlackChatPostMessageClient client = BuildClient(handler);

        SlackChatPostMessageResult result = await client.PostAsync(
            new SlackChatPostMessageRequest(TeamId, ChannelId, "hi", "corr-1"),
            CancellationToken.None);

        result.Kind.Should().Be(SlackChatPostMessageResultKind.NetworkFailure);
    }

    private static ISlackChatPostMessageClient BuildClient(
        StubHttpMessageHandler messageHandler,
        IReadOnlyDictionary<string, SlackWorkspaceConfig>? workspaces = null,
        ISecretProvider? secretsOverride = null)
    {
        StubHttpClientFactory factory = new(messageHandler);
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

        return new HttpClientSlackChatPostMessageClient(
            factory,
            store,
            secrets,
            NullLogger<HttpClientSlackChatPostMessageClient>.Instance,
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
