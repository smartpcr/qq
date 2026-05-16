// -----------------------------------------------------------------------
// <copyright file="SlackInboundControllerIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Stage 4.1 end-to-end integration tests that exercise the three
/// brief-mandated scenarios through the real
/// <see cref="Program"/> host bootstrapped via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>:
/// <list type="bullet">
///   <item><description>URL verification handshake -- POST
///   <c>/api/slack/events</c> with
///   <c>type = url_verification</c> and <c>challenge = abc123</c>
///   returns <c>{ "challenge": "abc123" }</c> with HTTP 200.</description></item>
///   <item><description>Slash command ACK within deadline -- POST
///   <c>/api/slack/commands</c> with a valid <c>/agent ask</c> payload
///   returns HTTP 200 AND the envelope lands in
///   <see cref="ISlackInboundQueue"/> for async
///   processing.</description></item>
///   <item><description>Interactive payload ACK -- POST
///   <c>/api/slack/interactions</c> with a Block Kit button click
///   returns HTTP 200 and the envelope lands in the queue.</description></item>
/// </list>
/// </summary>
public sealed class SlackInboundControllerIntegrationTests : IDisposable
{
    private const string TestTeamId = "T01TEST0001";
    private const string TestSigningSecretRef = "test://signing-secret/T01TEST0001";
    private const string TestSigningSecret = "8f742231b10e8888abcd99edabcd00d6";
    private const string TestChannelId = "C01TEST0001";
    private const string TestUserId = "U01TEST0001";
    private const string TestUserGroupId = "S01TEST0001";

    private readonly string sqliteDirectory;
    private readonly string sqlitePath;

    public SlackInboundControllerIntegrationTests()
    {
        this.sqliteDirectory = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-4.1-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.sqliteDirectory);
        this.sqlitePath = Path.Combine(this.sqliteDirectory, "audit.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.sqliteDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; an open SQLite handle on Windows
            // must not fail the test run.
        }
    }

    [Fact]
    public async Task Scenario_url_verification_handshake_returns_challenge_with_http_200()
    {
        // Brief scenario: Given a POST to /api/slack/events with
        // type = url_verification and challenge = abc123, When the
        // controller processes it, Then the response body is
        // { "challenge": "abc123" } with HTTP 200.
        using SlackTransportPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        const string body = "{\"type\":\"url_verification\",\"challenge\":\"abc123\",\"token\":\"xoxb\"}";

        using HttpRequestMessage request = BuildSignedJsonRequest("/api/slack/events", body);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Events API url_verification handshake MUST return HTTP 200");

        string responseBody = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(responseBody);
        doc.RootElement.GetProperty("challenge").GetString().Should().Be("abc123",
            "Slack expects {\"challenge\":\"<token>\"} verbatim per Events API spec");
    }

    [Fact]
    public async Task Scenario_slash_command_returns_http_200_and_enqueues_envelope()
    {
        // Brief scenario: Given a valid /agent ask slash command
        // payload, When posted to /api/slack/commands, Then HTTP 200
        // is returned and the envelope is enqueued for async
        // processing.
        using SlackTransportPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        string body = "token=xoxb"
            + $"&team_id={TestTeamId}"
            + $"&channel_id={TestChannelId}"
            + $"&user_id={TestUserId}"
            + "&command=%2Fagent"
            + "&text=ask+generate+implementation+plan+for+persistence+failover"
            + "&trigger_id=trig.AAA";

        using HttpRequestMessage request = BuildSignedFormRequest("/api/slack/commands", body);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Slack's 3-second ACK budget mandates HTTP 200 within deadline");

        ISlackInboundQueue queue = factory.Services.GetRequiredService<ISlackInboundQueue>();
        SlackInboundEnvelope envelope = await DequeueWithTimeoutAsync(queue);

        envelope.SourceType.Should().Be(SlackInboundSourceType.Command);
        envelope.TeamId.Should().Be(TestTeamId);
        envelope.ChannelId.Should().Be(TestChannelId);
        envelope.UserId.Should().Be(TestUserId);
        envelope.TriggerId.Should().Be("trig.AAA");
        envelope.IdempotencyKey.Should().Be(
            $"cmd:{TestTeamId}:{TestUserId}:/agent:trig.AAA",
            "architecture.md §3.4 keys slash commands by team:user:command:trigger_id");
    }

    [Fact]
    public async Task Scenario_interactive_payload_returns_http_200_and_enqueues_envelope()
    {
        // Brief scenario: Given a valid Block Kit button click
        // payload, When posted to /api/slack/interactions, Then
        // HTTP 200 is returned within the 3-second deadline.
        using SlackTransportPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        string json = $$"""
            {
              "type": "block_actions",
              "trigger_id": "trig.BUTTON",
              "team": { "id": "{{TestTeamId}}" },
              "channel": { "id": "{{TestChannelId}}" },
              "user": { "id": "{{TestUserId}}" },
              "actions": [ { "action_id": "approve_task_42", "value": "approve" } ]
            }
            """;
        string body = "payload=" + Uri.EscapeDataString(json);

        using HttpRequestMessage request = BuildSignedFormRequest("/api/slack/interactions", body);
        DateTime startedAt = DateTime.UtcNow;
        using HttpResponseMessage response = await client.SendAsync(request);
        TimeSpan elapsed = DateTime.UtcNow - startedAt;

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Block Kit button clicks require HTTP 200 within the 3-second ACK budget");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "the ACK MUST land within Slack's 3-second deadline so the user does not see a timeout");

        ISlackInboundQueue queue = factory.Services.GetRequiredService<ISlackInboundQueue>();
        SlackInboundEnvelope envelope = await DequeueWithTimeoutAsync(queue);

        envelope.SourceType.Should().Be(SlackInboundSourceType.Interaction);
        envelope.TeamId.Should().Be(TestTeamId);
        envelope.ChannelId.Should().Be(TestChannelId);
        envelope.UserId.Should().Be(TestUserId);
        envelope.TriggerId.Should().Be("trig.BUTTON");
        envelope.IdempotencyKey.Should().Be(
            $"interact:{TestTeamId}:{TestUserId}:approve_task_42:trig.BUTTON");
    }

    [Fact]
    public async Task Replayed_event_callback_produces_stable_idempotency_key()
    {
        // Brief acceptance criterion: "Slack event retries do not
        // duplicate agent tasks." The transport layer cannot enforce
        // deduplication (that lands in Stage 4.3) but it MUST emit
        // byte-stable idempotency keys across replays so the guard
        // recognises the duplicate.
        using SlackTransportPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        const string body = "{\"type\":\"event_callback\","
            + "\"event_id\":\"Ev_replay_check\","
            + "\"team_id\":\"" + TestTeamId + "\","
            + "\"event\":{\"type\":\"app_mention\",\"user\":\"" + TestUserId + "\",\"channel\":\"" + TestChannelId + "\"}}";

        // Fire twice with the same body (mimics Slack at-least-once
        // redelivery). Each request needs a fresh signature because
        // Slack stamps the timestamp on every retry, so we cannot
        // re-use the request object.
        using (HttpRequestMessage first = BuildSignedJsonRequest("/api/slack/events", body))
        using (HttpResponseMessage firstResp = await client.SendAsync(first))
        {
            firstResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (HttpRequestMessage second = BuildSignedJsonRequest("/api/slack/events", body))
        using (HttpResponseMessage secondResp = await client.SendAsync(second))
        {
            secondResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        ISlackInboundQueue queue = factory.Services.GetRequiredService<ISlackInboundQueue>();
        SlackInboundEnvelope first2 = await DequeueWithTimeoutAsync(queue);
        SlackInboundEnvelope second2 = await DequeueWithTimeoutAsync(queue);

        first2.IdempotencyKey.Should().Be("event:Ev_replay_check");
        second2.IdempotencyKey.Should().Be(first2.IdempotencyKey,
            "Stage 4.3's idempotency guard relies on the transport layer producing byte-identical keys across at-least-once retries");
    }

    private static async Task<SlackInboundEnvelope> DequeueWithTimeoutAsync(
        ISlackInboundQueue queue,
        int millisecondTimeout = 2000)
    {
        using CancellationTokenSource cts = new(millisecondTimeout);
        return await queue.DequeueAsync(cts.Token);
    }

    private static HttpRequestMessage BuildSignedJsonRequest(string path, string body)
    {
        StringContent content = new(body, Encoding.UTF8, "application/json");
        SignContent(content, body);
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
        return request;
    }

    private static HttpRequestMessage BuildSignedFormRequest(string path, string body)
    {
        StringContent content = new(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        SignContent(content, body);
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
        return request;
    }

    private static void SignContent(HttpContent content, string body)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string baseString = FormattableString.Invariant(
            $"{SlackSignatureValidator.VersionTag}:{timestamp}:{body}");
        string signature = $"{SlackSignatureValidator.VersionTag}={ComputeHexHmac(TestSigningSecret, baseString)}";
        content.Headers.TryAddWithoutValidation(SlackSignatureValidator.SignatureHeaderName, signature);
        content.Headers.TryAddWithoutValidation(
            SlackSignatureValidator.TimestampHeaderName,
            timestamp.ToString(CultureInfo.InvariantCulture));
    }

    private static string ComputeHexHmac(string secret, string baseString)
    {
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secret));
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        StringBuilder sb = new(digest.Length * 2);
        foreach (byte b in digest)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private SlackTransportPipelineFactory CreateFactory()
    {
        SlackTransportPipelineFactory factory = new(this.sqlitePath);

        InMemorySecretProvider inMemory =
            factory.Services.GetRequiredService<InMemorySecretProvider>();
        inMemory.Set(TestSigningSecretRef, TestSigningSecret);

        return factory;
    }

    private sealed class SlackTransportPipelineFactory : WebApplicationFactory<Program>
    {
        private readonly string sqlitePath;

        public SlackTransportPipelineFactory(string sqlitePath)
        {
            this.sqlitePath = sqlitePath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                Dictionary<string, string?> overrides = new()
                {
                    ["SecretProvider:ProviderType"] = "InMemory",
                    ["ConnectionStrings:" + Program.SlackAuditConnectionStringKey] =
                        $"Data Source={this.sqlitePath}",
                    ["Slack:Workspaces:0:TeamId"] = TestTeamId,
                    ["Slack:Workspaces:0:WorkspaceName"] = "Stage 4.1 Transport Test Workspace",
                    ["Slack:Workspaces:0:SigningSecretRef"] = TestSigningSecretRef,
                    ["Slack:Workspaces:0:BotTokenSecretRef"] = "test://bot-token/" + TestTeamId,
                    ["Slack:Workspaces:0:DefaultChannelId"] = TestChannelId,
                    ["Slack:Workspaces:0:AllowedChannelIds:0"] = TestChannelId,
                    ["Slack:Workspaces:0:AllowedUserGroupIds:0"] = TestUserGroupId,
                    ["Slack:Workspaces:0:Enabled"] = "true",
                };

                cfg.AddInMemoryCollection(overrides);
            });

            // Replace the production ISlackUserGroupClient
            // (SlackNetUserGroupClient calls the real Slack API) with a
            // test double that reports TestUserId as a member of
            // TestUserGroupId. Without this swap the Stage 3.2
            // SlackAuthorizationFilter would reject every test request
            // with UserNotInAllowedGroup -- yielding HTTP 200 (Slack
            // requires it) but never invoking the Stage 4.1
            // controllers. ConfigureTestServices runs AFTER the host's
            // ConfigureServices so this RemoveAll wins regardless of
            // the order TryAddSingleton was called in.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISlackUserGroupClient>();
                StubUserGroupClient stub = new();
                stub.SetMembers(TestTeamId, TestUserGroupId, TestUserId);
                services.AddSingleton<ISlackUserGroupClient>(stub);
            });
        }
    }

    private sealed class StubUserGroupClient : ISlackUserGroupClient
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Team, string Group), System.Collections.Generic.HashSet<string>> members =
            new();

        public void SetMembers(string teamId, string userGroupId, params string[] userIds)
        {
            this.members[(teamId, userGroupId)] = new System.Collections.Generic.HashSet<string>(
                userIds, StringComparer.Ordinal);
        }

        public Task<System.Collections.Generic.IReadOnlyCollection<string>> ListUserGroupMembersAsync(
            string teamId,
            string userGroupId,
            CancellationToken ct)
        {
            if (this.members.TryGetValue((teamId, userGroupId),
                out System.Collections.Generic.HashSet<string>? set))
            {
                System.Collections.Generic.IReadOnlyCollection<string> snapshot =
                    new System.Collections.Generic.List<string>(set);
                return Task.FromResult(snapshot);
            }

            return Task.FromResult<System.Collections.Generic.IReadOnlyCollection<string>>(
                Array.Empty<string>());
        }
    }
}
