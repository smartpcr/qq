// -----------------------------------------------------------------------
// <copyright file="RedactingHttpClientLoggerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 6.1 — pins for <see cref="RedactingHttpClientLogger"/> and
/// its DI wire-up via <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>.
/// Closes the regression seam the iter-1 acceptance run uncovered:
/// the default Microsoft.Extensions.Http logging handlers emit
/// "Start processing HTTP request {Method} {Uri}" at
/// <see cref="LogLevel.Information"/>, embedding the Telegram bot
/// token directly into structured logs. The fix is two-part:
/// <see cref="HttpClientLoggingExtensions.RemoveAllLoggers"/> on the
/// named <c>Telegram.Bot</c> HTTP client builder, plus
/// <see cref="HttpClientLoggingExtensions.AddLogger{TLogger}(Microsoft.Extensions.DependencyInjection.IHttpClientBuilder,bool)"/>
/// attaching the redacting logger. The tests below pin both halves.
/// </summary>
public sealed class RedactingHttpClientLoggerTests
{
    private const string SyntheticToken = "111111:integration-test-bot-token";
    private static readonly Uri TelegramSendMessageUri =
        new($"https://api.telegram.org/bot{SyntheticToken}/sendMessage?chat_id=1");

    [Fact]
    public void LogRequestStart_RedactsBotToken_BeforeMessageReachesSink()
    {
        var sink = new RecordingLogger();
        var logger = new RedactingHttpClientLogger(sink.AsLogger<RedactingHttpClientLogger>());

        using var request = new HttpRequestMessage(HttpMethod.Post, TelegramSendMessageUri);

        logger.LogRequestStart(request);

        sink.Entries.Should().NotBeEmpty();
        foreach (var entry in sink.Entries)
        {
            entry.Message.Should().NotContain(SyntheticToken,
                "Stage 6.1 \"Token excluded from logs\" — every log message emitted by the HTTP client logger MUST run the URL through TelegramHttpRedactor.Redact");
            entry.Message.Should().Contain(TelegramHttpRedactor.TokenRedaction,
                "the redacted placeholder confirms the redactor actually fired");
        }
    }

    [Fact]
    public void LogRequestStop_RedactsBotToken_BeforeMessageReachesSink()
    {
        var sink = new RecordingLogger();
        var logger = new RedactingHttpClientLogger(sink.AsLogger<RedactingHttpClientLogger>());

        using var request = new HttpRequestMessage(HttpMethod.Post, TelegramSendMessageUri);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        logger.LogRequestStop(context: null, request, response, TimeSpan.FromMilliseconds(123));

        sink.Entries.Should().NotBeEmpty();
        foreach (var entry in sink.Entries)
        {
            entry.Message.Should().NotContain(SyntheticToken);
            entry.Message.Should().Contain(TelegramHttpRedactor.TokenRedaction);
            entry.Message.Should().Contain("200",
                "the response status code is part of the diagnostic envelope and must survive the redaction pass");
        }
    }

    [Fact]
    public void LogRequestFailed_RedactsBotToken_BeforeMessageReachesSink()
    {
        var sink = new RecordingLogger();
        var logger = new RedactingHttpClientLogger(sink.AsLogger<RedactingHttpClientLogger>());

        using var request = new HttpRequestMessage(HttpMethod.Post, TelegramSendMessageUri);
        var ex = new HttpRequestException("connect timeout");

        logger.LogRequestFailed(context: null, request, response: null, ex, TimeSpan.FromMilliseconds(500));

        sink.Entries.Should().NotBeEmpty();
        foreach (var entry in sink.Entries)
        {
            entry.Message.Should().NotContain(SyntheticToken,
                "even the failure path must redact the URL — a network blip should never write the token to logs");
            entry.Message.Should().Contain(TelegramHttpRedactor.TokenRedaction);
            entry.Level.Should().Be(LogLevel.Error,
                "request failures map to Error so operators get a visible alert without losing the redacted URL");
        }
    }

    [Fact]
    public void AddTelegram_RegistersRedactingHttpClientLogger_OnNamedTelegramBotClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelegram(BuildConfig());
        using var sp = services.BuildServiceProvider();

        // The IHttpClientLogger DI wire-up surfaces through
        // RedactingHttpClientLogger being resolvable as a transient
        // service. If the AddLogger<T>() call regresses to the
        // default loggers, the type is no longer registered.
        var resolved = sp.GetService<RedactingHttpClientLogger>();
        resolved.Should().NotBeNull(
            "AddTelegram MUST register RedactingHttpClientLogger so AddLogger<T>() can resolve it on every HTTP message handler build — without this the framework throws InvalidOperationException at first outbound HTTP call");
    }

    private static IConfiguration BuildConfig()
    {
        var dict = new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SyntheticToken,
            ["Telegram:UsePolling"] = "true",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>
    /// In-memory <see cref="ILogger"/> that records every emitted
    /// log entry so the redaction assertions can scan the captured
    /// message/level pairs. Mirrors the shape used by
    /// <see cref="Stage61AcceptanceScenarioTests"/> but trimmed of
    /// scope-projection (this test pins the logger's own emit
    /// behavior, not scope propagation).
    /// </summary>
    private sealed class RecordingLogger
    {
        private readonly List<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries;

        public ILogger<T> AsLogger<T>() => new TypedLogger<T>(this);

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private sealed class TypedLogger<T> : ILogger<T>
        {
            private readonly RecordingLogger _owner;

            public TypedLogger(RecordingLogger owner)
            {
                _owner = owner;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _owner._entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }

            private sealed class NoopScope : IDisposable
            {
                public static readonly NoopScope Instance = new();

                public void Dispose() { }
            }
        }
    }
}
