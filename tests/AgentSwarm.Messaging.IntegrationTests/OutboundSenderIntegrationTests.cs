// -----------------------------------------------------------------------
// <copyright file="OutboundSenderIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Stage 7.1 integration tests for the Stage 2.3 outbound sender.
/// Each test boots the real Worker via
/// <see cref="TelegramTestFixture"/>, resolves
/// <see cref="IMessageSender"/> from the host's DI root, and asserts
/// the WireMock fake observed the expected Telegram Bot API call.
/// </summary>
/// <remarks>
/// <para>
/// These tests target end-to-end production wiring — they would catch
/// regressions where, for example, the bot client got registered with
/// the wrong lifetime, the rate limiter was not in the pipeline, or
/// the MarkdownV2 escaper stopped running.
/// </para>
/// <para>
/// Test isolation: each <c>[Fact]</c> uses its own fixture instance
/// (no <c>IClassFixture</c>) because <see cref="FakeTelegramApi"/>
/// records arrival order globally and the per-fact fresh fixture
/// keeps the assertion math simple.
/// </para>
/// </remarks>
public sealed class OutboundSenderIntegrationTests
{
    [Fact]
    public async Task Fixture_BootsWorker_HealthzReturns200()
    {
        // Stage 7.1 scenario: "Test fixture starts — Given the
        // TelegramTestFixture is instantiated, When the test host
        // starts, Then /healthz returns 200 and WireMock is listening".
        using var fixture = new TelegramTestFixture();
        using var client = fixture.CreateWorkerClient();

        using var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Stage 6.3 maps /healthz so the docker-compose / Kubernetes liveness probe has a target — a regression here means Worker bootstrap dropped MapHealthChecks");
        fixture.FakeApi.BaseUrl.Should().StartWith("http://",
            "WireMock-backed fake Telegram API must be listening on a local HTTP loopback URL");
    }

    [Fact]
    public async Task SendTextAsync_ReachesFakeTelegramApi_WithCorrelationFooter()
    {
        // Stage 7.1 scenario: "Fake API records calls — Given
        // FakeTelegramApi is configured, When SendTextAsync is invoked
        // via the connector, Then WireMock records the sendMessage
        // call with correct parameters".
        // Also asserts Stage 2.3 scenario "CorrelationId in message".
        //
        // The trace id is intentionally alphanumeric — TelegramMessageSender
        // MarkdownV2-escapes the entire chunk before posting, so a
        // hyphen-bearing id like "trace-001" would land on the wire as
        // "trace\-001". Using an unreserved id lets the test assert on
        // the exact correlation token without re-implementing the
        // escape table here. The MarkdownV2 escape contract is the
        // unit-test suite's job (see TelegramMessageSenderTests).
        using var fixture = new TelegramTestFixture();
        var sender = fixture.Services.GetRequiredService<IMessageSender>();
        const long chatId = 4242L;
        const string traceId = "traceint001";
        var text = "Deployment complete\n🔗 trace: " + traceId;

        var result = await sender.SendTextAsync(chatId, text, CancellationToken.None);

        result.Should().NotBeNull();
        result.TelegramMessageId.Should().BeGreaterThan(0L,
            "the fake API returns an auto-incrementing message_id; the sender must propagate it");

        var observed = fixture.FakeApi.SendMessageRequests;
        observed.Should().HaveCount(1,
            "exactly one sendMessage HTTP request should hit the fake API");
        observed[0].ChatId.Should().Be(chatId);
        observed[0].Text.Should().Contain(traceId,
            "Stage 2.3 step 6 mandates the trace/correlation id is carried in the message body");
        observed[0].ParseMode.Should().Be("MarkdownV2",
            "TelegramMessageSender posts with parse_mode=MarkdownV2");
    }

    [Fact]
    public async Task SendTextAsync_LongMessage_SplitsIntoMultipleChunks()
    {
        // Stage 2.3 scenario "Long message split into chunks":
        // 6000-char body must produce two ≤ 4096-char sendMessage
        // requests with the trace/correlation id on each.
        using var fixture = new TelegramTestFixture();
        var sender = fixture.Services.GetRequiredService<IMessageSender>();
        const long chatId = 5555L;
        var longBody = new string('a', 4000) + "\n\n" + new string('b', 2000)
            + "\n🔗 trace: trace-split";

        var result = await sender.SendTextAsync(chatId, longBody, CancellationToken.None);

        result.TelegramMessageId.Should().BeGreaterThan(0L);
        var observed = fixture.FakeApi.SendMessageRequests;
        observed.Should().HaveCountGreaterThan(1,
            "Stage 2.3 step 10: bodies > 4096 chars MUST be split before send");
        observed.Should().OnlyContain(r => r.Text.Length <= 4096,
            "every emitted chunk must stay within Telegram's 4096-char ceiling");
        observed.Should().AllSatisfy(r => r.ChatId.Should().Be(chatId),
            "every chunk targets the same chat");
    }

    [Fact]
    public async Task SendTextAsync_HonoursTelegram429RetryAfter_AndSucceedsOnRetry()
    {
        // Stage 2.3 scenario "Rate limit handled gracefully": the first
        // sendMessage attempt returns 429 with retry_after=1; the
        // sender must back off and retry rather than throwing. The
        // second attempt falls through to the default 200 stub.
        using var fixture = new TelegramTestFixture();
        fixture.FakeApi.StubRateLimitOnce(retryAfterSeconds: 1);
        var sender = fixture.Services.GetRequiredService<IMessageSender>();
        const long chatId = 6666L;

        var start = DateTimeOffset.UtcNow;
        var result = await sender.SendTextAsync(
            chatId,
            "after 429 retry",
            CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        result.TelegramMessageId.Should().BeGreaterThan(0L,
            "the second attempt must succeed and yield a positive message_id");
        elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900),
            "the sender must wait approximately retry_after seconds before retrying (1s with some scheduling jitter)");
        // At least 2 requests: one rejected with 429, one accepted with 200.
        fixture.FakeApi.SendMessageRequests.Count.Should().BeGreaterThanOrEqualTo(2,
            "sender must retry after a 429 — only one request would mean it gave up");
    }

    [Fact]
    public void HostBootstrap_ResolvesMessageSender_AsTelegramMessageSender()
    {
        // Regression guard: TelegramMessageSender must remain the
        // singleton bound to IMessageSender after the Stage 7.1
        // fixture's ITelegramBotClient override runs. A subtle bug
        // where the override accidentally clobbered IMessageSender or
        // where AddTelegram stopped registering it would break the
        // entire outbound pipeline silently.
        using var fixture = new TelegramTestFixture();

        var sender = fixture.Services.GetRequiredService<IMessageSender>();

        sender.Should().BeOfType<AgentSwarm.Messaging.Telegram.Sending.TelegramMessageSender>(
            "Stage 6.3 step 1 requires the Worker host to register the production TelegramMessageSender as IMessageSender");
    }
}
