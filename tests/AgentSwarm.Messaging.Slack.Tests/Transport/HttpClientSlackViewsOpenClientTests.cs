// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackViewsOpenClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 unit tests for the production
/// <see cref="HttpClientSlackViewsOpenClient"/>: drives the request
/// through a stub <see cref="HttpClient"/> + <see cref="IHttpClientFactory"/>
/// so the URL, Authorization header, JSON body, and the three
/// response-class branches (ok, slack error, transport failure) can be
/// pinned in isolation from the real Slack endpoint.
/// </summary>
public sealed class HttpClientSlackViewsOpenClientTests
{
    private const string TeamId = "T01TEAM";
    private const string SecretRef = "test://bot-token/T01TEAM";
    private const string BotToken = "xoxb-test-bot-token";

    [Fact]
    public async Task Successful_views_open_returns_Ok_and_sends_bearer_token_and_payload()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackViewsOpenClient client = BuildClient(handler);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, "trig.X", new { type = "modal" }),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Kind.Should().Be(SlackViewsOpenResultKind.Ok);

        handler.LastRequest!.RequestUri!.ToString().Should().Be(HttpClientSlackViewsOpenClient.ViewsOpenUrl);
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(BotToken);
        handler.LastRequestBody!.Should().Contain("\"trigger_id\":\"trig.X\"");
        handler.LastRequestBody.Should().Contain("\"view\":");
    }

    [Fact]
    public async Task Slack_returns_ok_false_surfaces_SlackError()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"invalid_trigger_id\"}"));
        ISlackViewsOpenClient client = BuildClient(handler);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, "trig.X", new { type = "modal" }),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(SlackViewsOpenResultKind.SlackError);
        result.Error.Should().Be("invalid_trigger_id");
    }

    [Fact]
    public async Task Non_2xx_HTTP_status_surfaces_NetworkFailure()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.InternalServerError, "boom"));
        ISlackViewsOpenClient client = BuildClient(handler);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, "trig.X", new { type = "modal" }),
            CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.NetworkFailure);
    }

    [Fact]
    public async Task Missing_workspace_short_circuits_with_MissingConfiguration_and_does_not_make_HTTP_call()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        ISlackViewsOpenClient client = BuildClient(handler, workspaces: new Dictionary<string, SlackWorkspaceConfig>());

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest("T_UNKNOWN", "trig.X", new { type = "modal" }),
            CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull("the HTTP call must be skipped when the workspace is unknown");
    }

    [Fact]
    public async Task Empty_bot_token_short_circuits_with_MissingConfiguration()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        StubSecretProvider secrets = new();
        secrets.Set(SecretRef, string.Empty);

        ISlackViewsOpenClient client = BuildClient(handler, secretsOverride: secrets);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, "trig.X", new { type = "modal" }),
            CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.MissingConfiguration);
        handler.LastRequest.Should().BeNull("an empty bot token must not result in an unauthenticated views.open call");
    }

    /// <summary>
    /// Stage 4.1 iter-3 / iter-4 evaluator item 3 regression pin:
    /// when the caller's CancellationToken is cancelled DURING bot-token
    /// resolution, the client MUST re-throw <see cref="OperationCanceledException"/>
    /// rather than swallow it into the broad <c>catch (Exception)</c>
    /// that converts secret-resolution failures into a
    /// <see cref="SlackViewsOpenResultKind.MissingConfiguration"/>
    /// ephemeral error. Misclassifying a cancelled request as a
    /// missing-config error would (a) confuse operators with a fake
    /// configuration alert and (b) hide the fact that the upstream
    /// request was aborted.
    /// </summary>
    [Fact]
    public async Task Caller_cancelled_token_during_bot_token_resolution_propagates_OperationCanceledException()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        CancellingSecretProvider secrets = new();

        ISlackViewsOpenClient client = BuildClient(handler, secretsOverride: secrets);

        using CancellationTokenSource cts = new();

        Func<Task> act = async () =>
        {
            await client.OpenAsync(
                new SlackViewsOpenRequest(TeamId, "trig.X", new { type = "modal" }),
                cts.Token);
        };

        // The secrets provider cancels the supplied token as soon as
        // it is invoked, then throws OperationCanceledException -- this
        // is the exact race the OCE filter at HttpClientSlackViewsOpenClient.cs:135
        // is designed to honour.
        secrets.OnGetSecret = providedCt =>
        {
            cts.Cancel();
            providedCt.ThrowIfCancellationRequested();
        };

        await act.Should().ThrowAsync<OperationCanceledException>(
            "OperationCanceledException MUST propagate when the caller's token is cancelled; the broad catch must NOT convert it to MissingConfiguration");

        handler.LastRequest.Should().BeNull(
            "the HTTP call must not be attempted when the secret resolution was cancelled");
    }

    /// <summary>
    /// Defence-in-depth: a non-caller-token OCE (e.g., the secret
    /// provider's own internal timeout) must NOT propagate because
    /// the filter at <c>HttpClientSlackViewsOpenClient.cs:135</c>
    /// is intentionally scoped to <c>when (ct.IsCancellationRequested)</c>.
    /// Such a failure should still surface as
    /// <see cref="SlackViewsOpenResultKind.MissingConfiguration"/>.
    /// </summary>
    [Fact]
    public async Task Internal_OperationCanceledException_with_live_caller_token_still_returns_MissingConfiguration()
    {
        StubHttpMessageHandler handler = new(
            (req, _) => CreateResponse(HttpStatusCode.OK, "{\"ok\":true}"));
        CancellingSecretProvider secrets = new()
        {
            OnGetSecret = _ =>
            {
                // Throw OCE with a token that is NOT the caller's.
                using CancellationTokenSource internalCts = new();
                internalCts.Cancel();
                throw new OperationCanceledException(internalCts.Token);
            },
        };

        ISlackViewsOpenClient client = BuildClient(handler, secretsOverride: secrets);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, "trig.X", new { type = "modal" }),
            CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.MissingConfiguration,
            "the OCE filter is scoped to ct.IsCancellationRequested; an internal OCE that does not match the caller's token must be classified as a missing-config error, not propagated");
    }

    private static ISlackViewsOpenClient BuildClient(
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

        return new HttpClientSlackViewsOpenClient(
            factory,
            store,
            secrets,
            NullLogger<HttpClientSlackViewsOpenClient>.Instance,
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

    /// <summary>
    /// Test double that runs a caller-supplied callback against every
    /// <see cref="GetSecretAsync"/> invocation. Used to pin the OCE
    /// propagation contract on <see cref="HttpClientSlackViewsOpenClient"/>.
    /// </summary>
    private sealed class CancellingSecretProvider : ISecretProvider
    {
        public Action<CancellationToken>? OnGetSecret { get; set; }

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            this.OnGetSecret?.Invoke(ct);
            return Task.FromResult("xoxb-unreached");
        }
    }
}
