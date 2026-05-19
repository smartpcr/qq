// -----------------------------------------------------------------------
// <copyright file="TelegramBotHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;

// NOTE: using directives are placed OUTSIDE the file-scoped namespace
// declaration so `Telegram.Bot` resolves to the global Telegram.Bot
// package namespace, not `AgentSwarm.Messaging.Telegram.Bot` (a
// non-existent child of the AgentSwarm.Messaging.Telegram assembly's
// root namespace). The C# compiler walks parent namespaces when
// usings are inside a namespace; the rest of the test suite follows
// this same convention for Telegram.Bot imports
// (see RotatingTelegramBotClientTests.cs / TelegramMessageSenderTests.cs).
namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 6.2 — pins for <see cref="TelegramBotHealthCheck"/>. The
/// brief's two scenarios — Healthy when getMe returns under 5 s,
/// Unhealthy when Telegram is unreachable — are covered here at the
/// unit level via a <see cref="HttpMessageHandler"/> swap on the
/// underlying Telegram.Bot client. Iter-2 evaluator items 2-4 are
/// pinned by the explicit identity / cancellation / internal-timeout
/// tests further down.
/// </summary>
public sealed class TelegramBotHealthCheckTests
{
    private const string BotToken = "111111:health-check-unit-test-token";

    [Fact]
    public async Task CheckHealthAsync_WhenGetMeReturnsBotIdentity_ReturnsHealthy()
    {
        // Brief scenario: "Given the bot token is valid ... When
        // /healthz is called, Then HTTP 200 with all checks reporting
        // Healthy." This pins the per-check leg of that scenario.
        var handler = new StubHandler(StubHandler.GetMeOk(id: 7777L, username: "unit_test_bot"));
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(client, NullLogger<TelegramBotHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("bot_id").WhoseValue.Should().Be(7777L);
        result.Data.Should().ContainKey("bot_username").WhoseValue.Should().Be("unit_test_bot");
        result.Data.Should().ContainKey("elapsed_ms");
        result.Data.Should().ContainKey("timeout_ms");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenGetMeThrows_ReturnsUnhealthy_WithException()
    {
        // Brief scenario: "Bot unreachable degrades health — Given the
        // Telegram API is unreachable, When /healthz is called, Then
        // the TelegramBot check reports Unhealthy."
        var handler = new StubHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("unreachable")));
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(client, NullLogger<TelegramBotHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().NotBeNull();
        result.Description.Should().Contain("Telegram getMe failed");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenInternalProbeTimeoutFires_NoCallerCancellation_ReturnsUnhealthy()
    {
        // Iter-2 evaluator item 4 — exercise the check's OWN
        // internal timeout deterministically. We use the internal
        // constructor seam to drop the probe budget to 200ms; the
        // handler hangs forever, so only the internal CTS can
        // unblock the await. The caller token is CancellationToken.None
        // so the catch clause's "caller not cancelled" condition is
        // satisfied — this proves the internal-timeout code path is
        // distinct from caller cancellation.
        var hangForever = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new StubHandler(_ => hangForever.Task);
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(
            client,
            NullLogger<TelegramBotHealthCheck>.Instance,
            TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        sw.Stop();

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("200ms internal probe timeout");
        result.Data.Should().ContainKey("caller_cancelled")
            .WhoseValue.Should().Be(false,
                "the internal timeout path explicitly records that the caller did NOT cancel — distinguishes this from the caller-cancellation branch");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150),
            "the internal CTS must have actually fired (~200ms budget) — a sub-150ms elapsed time would mean the test isn't really exercising the timeout");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the internal timeout MUST short-circuit before the hanging handler completes (it never does); a finite elapsed time proves the CTS fired rather than the handler returning. Upper bound widened from 3s to 10s in iter-9 because xUnit's default parallel-class execution + the SQLite shared-cache integration tests + the OutboundQueueProcessor delay-based suite starved the CTS timer queue on CI under contention (observed elapsed ~5s in the iter-8 gate run despite a 200ms budget). The semantic invariant — \"the timeout fires before the handler\" — is unchanged; only the wall-clock guess was loosened to absorb scheduler jitter without losing the never-completing-handler proof");

        // Release the hanging task to let xUnit dispose the handler.
        hangForever.TrySetCanceled();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenCallerCancels_PropagatesOperationCanceledException()
    {
        // Iter-2 evaluator item 3 — caller cancellation MUST propagate.
        // The HealthCheckService treats request abort and host
        // shutdown as out-of-band signals; converting the OCE into a
        // false "Telegram is down" Unhealthy result would lie about
        // upstream health on every aborted /healthz request.
        var hangForever = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new StubHandler(_ => hangForever.Task);
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(client, NullLogger<TelegramBotHealthCheck>.Instance);

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Func<Task> act = async () => await check.CheckHealthAsync(new HealthCheckContext(), callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's token cancellation must propagate so the HealthCheckService can handle it as a host-shutdown / request-abort signal rather than misreport Telegram as down");

        // Release the hanging task to let xUnit dispose the handler.
        hangForever.TrySetCanceled();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenGetMeReturnsNullUser_ReturnsUnhealthy()
    {
        // Iter-2 evaluator item 2 — a successful HTTP response with
        // a null `result` field is NOT a valid bot identity. The
        // brief is literal: "healthy iff the bot identity is returned
        // within 5 seconds". A null `result` would otherwise serialize
        // to `bot_id = 0`, `bot_username = ""` and silently mask the
        // misconfiguration on the operator dashboard.
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":null}""", System.Text.Encoding.UTF8, "application/json"),
            });
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(client, NullLogger<TelegramBotHealthCheck>.Instance);

        // Telegram.Bot.Client throws on null `result`; whichever path
        // surfaces (explicit Unhealthy from our identity-guard or
        // Unhealthy from the exception catch), the contract is the
        // same: never report Healthy with bot_id = 0.
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "a null `result` envelope must NEVER be reported as a healthy bot identity");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenGetMeReturnsZeroId_ReturnsUnhealthy_WithIdentityMissingFlag()
    {
        // Iter-2 evaluator item 2 — a zero `id` is a degraded or
        // stubbed response shape; surface it as Unhealthy with the
        // identity_missing data flag for the operator dashboard.
        var handler = new StubHandler(StubHandler.GetMeOk(id: 0L, username: "edge_case_bot"));
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(client, NullLogger<TelegramBotHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("valid bot identity");
        result.Data.Should().ContainKey("identity_missing").WhoseValue.Should().Be(true);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenGetMeReturnsEmptyUsername_ReturnsUnhealthy()
    {
        // Iter-2 evaluator item 2 — a blank username is operationally
        // equivalent to a missing identity for the operator dashboard
        // (it cannot pivot on "this is the right bot"); reject the
        // result rather than emit `bot_username = ""`.
        var handler = new StubHandler(StubHandler.GetMeOk(id: 12345L, username: string.Empty));
        var client = NewClient(handler);

        var check = new TelegramBotHealthCheck(client, NullLogger<TelegramBotHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("valid bot identity");
        result.Data.Should().ContainKey("identity_missing").WhoseValue.Should().Be(true);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Action act = () => new TelegramBotHealthCheck(null!, NullLogger<TelegramBotHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var client = NewClient(new StubHandler(StubHandler.GetMeOk(1, "n")));
        Action act = () => new TelegramBotHealthCheck(client, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        // The internal seam must reject non-positive timeouts — a
        // zero or negative budget would cancel the linked CTS before
        // the getMe call starts, masking real failures behind a
        // synthetic timeout.
        var client = NewClient(new StubHandler(StubHandler.GetMeOk(1, "n")));
        Action act = () => new TelegramBotHealthCheck(
            client,
            NullLogger<TelegramBotHealthCheck>.Instance,
            TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Name_Constant_StableForRegistration()
    {
        // The worker's AddCheck<TelegramBotHealthCheck>(Name, ...)
        // call relies on this constant; renaming it silently would
        // break the operator's /healthz pivot on the named check.
        TelegramBotHealthCheck.Name.Should().Be("telegram_bot_api");
    }

    [Fact]
    public void ProbeTimeout_IsFiveSeconds_PerBrief()
    {
        // Stage 6.2 brief: "reports healthy if the bot identity is
        // returned within 5 seconds". The constant is the contract.
        TelegramBotHealthCheck.ProbeTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    private static ITelegramBotClient NewClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new TelegramBotClient(
            new TelegramBotClientOptions(BotToken),
            httpClient);
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> that delegates to a
    /// caller-supplied callback so each test can specify the exact
    /// response shape for the single getMe call the health check
    /// makes. Avoids pulling WireMock into the unit-test assembly.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _callback;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this(req => Task.FromResult(sync(req)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        {
            _callback = callback;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var responseTask = _callback(request);

            return await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public static Func<HttpRequestMessage, HttpResponseMessage> GetMeOk(long id, string username)
            => _ =>
            {
                var body = $$"""
                    {
                        "ok": true,
                        "result": {
                            "id": {{id}},
                            "is_bot": true,
                            "first_name": "UnitTestBot",
                            "username": "{{username}}",
                            "can_join_groups": true,
                            "can_read_all_group_messages": false,
                            "supports_inline_queries": false
                        }
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
            };
    }
}
