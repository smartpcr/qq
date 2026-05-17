// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackThreadedReplyPosterTests.cs" company="Microsoft Corp.">
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
/// Stage 5.2 unit tests for the production
/// <see cref="HttpClientSlackThreadedReplyPoster"/>: drives the request
/// through a stub <see cref="HttpClient"/> + <see cref="IHttpClientFactory"/>
/// so the URL, Authorization header, JSON body, and the failure
/// branches (network, slack-error, missing-config, malformed-response,
/// caller-cancellation) can be pinned without touching Slack.
/// Mirrors the test pattern established for the Stage 4.1
/// <c>HttpClientSlackViewsOpenClient</c>.
/// </summary>
public sealed class HttpClientSlackThreadedReplyPosterTests
{
    private const string TeamId = "T01TEAM";
    private const string ChannelId = "C01CHAN";
    private const string ThreadTs = "1700000123.000200";
    private const string SecretRef = "test://bot-token/T01TEAM";
    private const string BotToken = "xoxb-test-bot-token";

    [Fact]
    public async Task Posts_to_chat_postMessage_with_bearer_token_channel_thread_and_text()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true,\"ts\":\"1700000200.000300\"}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "Task T-1 created.", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be(HttpClientSlackThreadedReplyPoster.ChatPostMessageUrl);
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(BotToken);

        handler.LastRequestBody.Should().Contain($"\"channel\":\"{ChannelId}\"");
        handler.LastRequestBody.Should().Contain($"\"thread_ts\":\"{ThreadTs}\"");
        handler.LastRequestBody.Should().Contain("\"text\":\"Task T-1 created.\"");
    }

    [Fact]
    public async Task Posts_without_thread_ts_when_request_thread_ts_is_null()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs: null, "hello", "corr-1"),
            CancellationToken.None);

        handler.LastRequestBody.Should().NotContain("\"thread_ts\"",
            "the HTTP body MUST omit thread_ts entirely when the request's ThreadTs is null so Slack posts a top-level message rather than referencing a non-existent thread");
        handler.LastRequestBody.Should().Contain("\"channel\":\"" + ChannelId + "\"");
        handler.LastRequestBody.Should().Contain("\"text\":\"hello\"");
    }

    [Fact]
    public async Task Slack_ok_false_response_is_swallowed_and_logged_not_thrown()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"channel_not_found\"}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        Func<Task> act = async () => await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            "ok:false MUST be swallowed -- propagating would dead-letter the inbound envelope and duplicate orchestrator side-effects on retry (FR-008 / item 2 contract)");
    }

    [Fact]
    public async Task Non_2xx_HTTP_status_is_swallowed_and_logged_not_thrown()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.InternalServerError, "boom"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        Func<Task> act = async () => await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Malformed_response_body_is_swallowed_and_logged_not_thrown()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "not-json"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        Func<Task> act = async () => await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HttpRequestException_is_swallowed_and_logged_not_thrown()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => throw new HttpRequestException("connection refused"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        Func<Task> act = async () => await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            "transport failures MUST be swallowed so an at-least-once Slack inbound retry does NOT replay CreateTaskAsync / PublishDecisionAsync");
    }

    [Fact]
    public async Task Missing_workspace_short_circuits_and_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(
            handler,
            workspaces: new Dictionary<string, SlackWorkspaceConfig>());

        await poster.PostAsync(
            new SlackThreadedReplyRequest("T_UNKNOWN", ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().BeNull(
            "the HTTP call MUST be skipped when the workspace cannot be resolved -- no point posting without an auth token");
    }

    [Fact]
    public async Task Disabled_workspace_short_circuits_and_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(
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

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Empty_bot_token_short_circuits_and_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        StubSecretProvider secrets = new();
        secrets.Set(SecretRef, string.Empty);
        ISlackThreadedReplyPoster poster = BuildPoster(handler, secretsOverride: secrets);

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().BeNull(
            "an empty bot token MUST NOT result in an unauthenticated chat.postMessage call");
    }

    [Fact]
    public async Task Missing_team_id_short_circuits_and_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId: string.Empty, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Missing_channel_id_short_circuits_and_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId: string.Empty, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Empty_text_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, Text: string.Empty, "corr-1"),
            CancellationToken.None);

        handler.LastRequest.Should().BeNull(
            "posting an empty body to Slack would log a noisy ok:false; short-circuit instead");
    }

    [Fact]
    public async Task Caller_cancelled_token_propagates_OperationCanceledException()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackThreadedReplyPoster poster = BuildPoster(handler);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller-cancelled tokens MUST propagate so the ingestor's shutdown loop honours cancellation");

        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Secret_resolution_failure_is_swallowed_when_caller_token_is_live()
    {
        StubHttpMessageHandler handler = new(
            (_, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        StubSecretProvider secrets = new(); // no value set -> throws SecretNotFoundException
        ISlackThreadedReplyPoster poster = BuildPoster(handler, secretsOverride: secrets);

        Func<Task> act = async () => await poster.PostAsync(
            new SlackThreadedReplyRequest(TeamId, ChannelId, ThreadTs, "hi", "corr-1"),
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            "secret resolution failures MUST be swallowed so the inbound pipeline does NOT retry and replay orchestrator side-effects");

        handler.LastRequest.Should().BeNull();
    }

    private static ISlackThreadedReplyPoster BuildPoster(
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

        return new HttpClientSlackThreadedReplyPoster(
            factory,
            store,
            secrets,
            NullLogger<HttpClientSlackThreadedReplyPoster>.Instance,
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
            return Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(new List<SlackWorkspaceConfig>(this.workspaces.Values));
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
