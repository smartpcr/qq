// -----------------------------------------------------------------------
// <copyright file="HealthCheckEndpointIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Stage 6.2 — end-to-end integration tests for the
/// <c>/healthz</c> endpoint wired in
/// <c>AgentSwarm.Messaging.Worker.Program</c>. Covers the brief's two
/// test scenarios literally:
/// <list type="number">
///   <item><description>
///   "Healthy system — Given the bot token is valid, queue is empty,
///   and DB is reachable, When <c>/healthz</c> is called, Then HTTP
///   200 with all checks reporting Healthy".
///   </description></item>
///   <item><description>
///   "Bot unreachable degrades health — Given the Telegram API is
///   unreachable, When <c>/healthz</c> is called, Then the
///   <c>TelegramBot</c> check reports Unhealthy and overall status
///   is Unhealthy".
///   </description></item>
/// </list>
/// Asserts on the JSON detail body — the brief mandates "JSON detail
/// output" — so a regression that drops the
/// <see cref="AgentSwarm.Messaging.Worker.Observability.HealthCheckJsonResponseWriter"/>
/// would surface as a failed body-shape assertion.
/// </summary>
public sealed class HealthCheckEndpointIntegrationTests
{
    [Fact]
    public async Task HealthzEndpoint_WhenAllChecksPass_Returns200_WithJsonHealthyBody()
    {
        // Stage 6.2 scenario 1: "Healthy system" — the
        // TelegramTestFixture's FakeTelegramApi already stubs getMe
        // (200 OK), the SQLite shared-cache DB is created on startup
        // by TestSchemaInitializer, and no outbound rows are seeded
        // so the queue depth is zero.
        using var fixture = new TelegramTestFixture();
        using var client = fixture.CreateWorkerClient();

        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Stage 6.2 brief: 'HTTP 200 with all checks reporting Healthy'. Body was: " + body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json",
            "Stage 6.2 brief: 'expose at /healthz with JSON detail output'");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        root.GetProperty("status").GetString().Should().Be("Healthy",
            "all four checks (DLQ, TelegramBot, OutboundQueue, Database) must report Healthy");

        var entries = root.GetProperty("entries");
        entries.TryGetProperty("telegram_bot_api", out _).Should().BeTrue(
            "the TelegramBotHealthCheck must appear in the /healthz JSON entries under the e2e-scenarios.md canonical name 'telegram_bot_api'");
        entries.TryGetProperty("outbound_queue", out _).Should().BeTrue(
            "the OutboundQueueHealthCheck must appear in the /healthz JSON entries");
        entries.TryGetProperty("database", out _).Should().BeTrue(
            "the DatabaseHealthCheck must appear in the /healthz JSON entries");
        entries.TryGetProperty("outbound_dead_letter_queue_depth", out _).Should().BeTrue(
            "the Stage 4.2 DeadLetterQueueHealthCheck must continue to appear (defense-in-depth)");

        // Every entry must individually report Healthy.
        foreach (var entry in entries.EnumerateObject())
        {
            entry.Value.GetProperty("status").GetString().Should().Be(
                "Healthy",
                $"entry '{entry.Name}' must report Healthy in the all-clear scenario");
        }
    }

    [Fact]
    public async Task HealthzEndpoint_WhenTelegramApiUnreachable_Returns503_WithTelegramBotUnhealthy()
    {
        // Stage 6.2 scenario 2: "Bot unreachable degrades health".
        // Configure the fake Telegram API to return HTTP 500 on
        // getMe; the TelegramBotHealthCheck's GetMeAsync call will
        // throw and the check will report Unhealthy, which rolls up
        // to the overall /healthz response per ASP.NET Core defaults.
        using var fixture = new TelegramTestFixture();
        fixture.FakeApi.StubGetMeFailure();

        using var client = fixture.CreateWorkerClient();

        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "Stage 6.2 brief: when TelegramBot reports Unhealthy the overall status MUST be Unhealthy → 503. Body was: " + body);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        root.GetProperty("status").GetString().Should().Be("Unhealthy");

        var entries = root.GetProperty("entries");
        entries.GetProperty("telegram_bot_api").GetProperty("status").GetString().Should().Be(
            "Unhealthy",
            "the TelegramBot check must report Unhealthy when getMe fails");
    }

    [Fact]
    public async Task HealthzEndpoint_BodyShape_ContainsTotalDurationAndPerCheckMetadata()
    {
        // Stage 6.2 brief: "JSON detail output". Pins the exact body
        // shape the operator dashboard parses — status, totalDuration,
        // and per-entry status/duration/tags so a regression to the
        // framework's plain-text default would fail here.
        using var fixture = new TelegramTestFixture();
        using var client = fixture.CreateWorkerClient();

        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        root.TryGetProperty("status", out _).Should().BeTrue();
        root.TryGetProperty("totalDuration", out _).Should().BeTrue();
        root.TryGetProperty("entries", out var entries).Should().BeTrue();

        // Pick the telegram_bot_api entry as the canonical specimen.
        var telegramEntry = entries.GetProperty("telegram_bot_api");
        telegramEntry.TryGetProperty("status", out _).Should().BeTrue();
        telegramEntry.TryGetProperty("duration", out _).Should().BeTrue();
        telegramEntry.TryGetProperty("tags", out var tags).Should().BeTrue();
        tags.ValueKind.Should().Be(JsonValueKind.Array,
            "entry tags must serialise as a JSON array so the operator dashboard can pivot on them");
    }
}

