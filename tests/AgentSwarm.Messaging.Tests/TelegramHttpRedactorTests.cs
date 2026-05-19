// -----------------------------------------------------------------------
// <copyright file="TelegramHttpRedactorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using FluentAssertions;

/// <summary>
/// Stage 6.1 — pure-function pins for
/// <see cref="TelegramHttpRedactor"/>. This is the load-bearing
/// piece of the "Token excluded from logs" acceptance scenario: the
/// OpenTelemetry HTTP-client instrumentation calls
/// <c>Redact(request.RequestUri)</c> before the URL reaches any
/// <c>url.full</c> / <c>http.url</c> span attribute, so a regression
/// here would directly leak the bot token into an OTLP collector.
/// </summary>
public sealed class TelegramHttpRedactorTests
{
    private const string SyntheticToken = "111111:integration-test-bot-token";
    private const string ProductionShapedToken = "5234567890:AAFmJgKZ-AhJfFoo_examplebotApiToken";

    [Theory]
    [InlineData("https://api.telegram.org/bot111111:integration-test-bot-token/sendMessage")]
    [InlineData("https://api.telegram.org/bot111111:integration-test-bot-token/getMe")]
    [InlineData("https://api.telegram.org/bot111111:integration-test-bot-token/setWebhook")]
    [InlineData("https://api.telegram.org/bot111111:integration-test-bot-token/sendMessage?chat_id=1")]
    public void Redact_ReplacesToken_OnEveryBotApiMethodPath(string url)
    {
        var redacted = TelegramHttpRedactor.Redact(url);

        redacted.Should().NotContain(SyntheticToken,
            "the token segment must never survive into a recorded URL — the redactor IS the OTLP-leak gate");
        redacted.Should().Contain(TelegramHttpRedactor.TokenRedaction,
            "the marker placeholder confirms the redactor actually fired (rather than silently passing the URL through)");
        // The method name AFTER the token must survive so traces are still useful.
        var slashIndex = url.LastIndexOf('/');
        var methodWithQuery = url.Substring(slashIndex);
        var method = methodWithQuery.Split('?')[0];
        redacted.Should().Contain(method,
            "the Bot API method name (sendMessage/getMe/...) must be preserved so the trace is still diagnostic — only the token segment is redacted");
    }

    [Fact]
    public void Redact_HandlesProductionShapedTokens()
    {
        var url = $"https://api.telegram.org/bot{ProductionShapedToken}/sendMessage";

        var redacted = TelegramHttpRedactor.Redact(url);

        redacted.Should().NotContain(ProductionShapedToken,
            "production tokens with underscores / dashes must still be redacted (the regex MUST match the full segment, not just alphanumeric runs)");
        redacted.Should().Be("https://api.telegram.org/bot" + TelegramHttpRedactor.TokenRedaction + "/sendMessage");
    }

    [Fact]
    public void Redact_PassesThrough_NonBotApiUrls()
    {
        const string url = "https://example.com/api/v1/users/1234";

        var redacted = TelegramHttpRedactor.Redact(url);

        redacted.Should().Be(url,
            "URLs that do not match the /bot{TOKEN}/ family must pass through unchanged so we never accidentally mangle non-Telegram traffic");
    }

    [Fact]
    public void Redact_Uri_ReturnsRedactedAbsoluteUri()
    {
        var uri = new Uri($"https://api.telegram.org/bot{SyntheticToken}/sendMessage");

        var redacted = TelegramHttpRedactor.Redact(uri);

        redacted.Should().NotContain(SyntheticToken);
        redacted.Should().StartWith("https://api.telegram.org/bot" + TelegramHttpRedactor.TokenRedaction + "/");
    }

    [Fact]
    public void Redact_NullUri_ReturnsEmptyString()
    {
        TelegramHttpRedactor.Redact((Uri?)null).Should().BeEmpty(
            "null defensively returns empty so the OTel hook can write the tag without a NRE — the HTTP client may produce a null RequestUri in some retry / DNS-failure paths");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Redact_NullOrEmpty_ReturnsInputUnchanged(string? input)
    {
        TelegramHttpRedactor.Redact(input!).Should().Be(input,
            "empty / null inputs pass through; the redactor is purely additive on the failure path");
    }

    [Fact]
    public void Redact_TokenAppearingAsQueryString_IsNotRemoved_OutsideBotPath()
    {
        // The redactor's contract is narrowly scoped to the
        // /bot{TOKEN}/ path family. A bot SDK that ever started
        // sending tokens via query string would need a NEW redaction
        // rule; we intentionally do NOT widen the regex to
        // pattern-match arbitrary "looks like a token" substrings
        // because that would risk false-positive redactions on
        // unrelated URLs (e.g. correlation ids).
        const string url = "https://example.com/foo?bot=111111:not-actually-redacted";

        TelegramHttpRedactor.Redact(url).Should().Be(url);
    }
}
