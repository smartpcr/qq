// -----------------------------------------------------------------------
// <copyright file="RedactingHttpClientLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Diagnostics;

using System;
using System.Net.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 6.1 — replacement for the default
/// <see cref="Microsoft.Extensions.Http"/> logging handlers on the
/// <c>Telegram.Bot</c> named <see cref="HttpClient"/>. The Telegram
/// Bot API embeds the bearer bot token directly in the URL path
/// (<c>https://api.telegram.org/bot{TOKEN}/sendMessage</c>), and the
/// stock <c>LoggingHttpMessageHandler</c> / <c>LoggingScopeHttpMessageHandler</c>
/// emit "Start processing HTTP request {Method} {Uri}" at
/// <see cref="LogLevel.Information"/> — which would write the token
/// to every operator's logs verbatim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-up.</b> Used via
/// <c>builder.RemoveAllLoggers().AddLogger&lt;RedactingHttpClientLogger&gt;()</c>
/// in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>.
/// <see cref="HttpClientLoggingExtensions.RemoveAllLoggers"/> strips
/// BOTH default logging handlers (logical + client), then
/// <see cref="HttpClientLoggingExtensions.AddLogger{TLogger}(Microsoft.Extensions.DependencyInjection.IHttpClientBuilder,bool)"/>
/// attaches this implementation in their place. The two are paired
/// for a reason: without the <c>RemoveAllLoggers</c> first, the
/// default loggers would still emit the raw URL alongside our
/// redacted line and the Stage 6.1 acceptance scenario 2 ("Token
/// excluded from logs") would fail because some other log entry —
/// emitted by the framework — leaks the secret.
/// </para>
/// <para>
/// <b>Defence in depth.</b> The OpenTelemetry HTTP-client
/// instrumentation (configured in
/// <c>OpenTelemetrySetup.AddTelegramOpenTelemetry</c>) already
/// redacts <c>url.full</c> and <c>http.url</c> activity tags via the
/// <see cref="TelegramHttpRedactor"/> path. This logger closes the
/// separate <see cref="ILogger"/> seam so the bot token cannot reach
/// a sink through EITHER the span exporter OR the standard log
/// pipeline (Console / File / Seq / App Insights / OpenTelemetry
/// logs exporter, etc.).
/// </para>
/// <para>
/// <b>Log shape.</b> Mirrors the verbs / structure of the default
/// Microsoft.Extensions.Http loggers so existing dashboards keying
/// off "Sending HTTP request" continue to work, with the URL value
/// passed through <see cref="TelegramHttpRedactor.Redact(Uri?)"/>
/// before it lands on the message template. The
/// <c>StatusCode</c> / <c>ElapsedMilliseconds</c> structured
/// properties match the default logger so latency dashboards and
/// error counters keep their query shape.
/// </para>
/// </remarks>
public sealed class RedactingHttpClientLogger : IHttpClientLogger
{
    private readonly ILogger<RedactingHttpClientLogger> _logger;

    public RedactingHttpClientLogger(ILogger<RedactingHttpClientLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public object? LogRequestStart(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var redacted = TelegramHttpRedactor.Redact(request.RequestUri);
        _logger.LogInformation(
            "Sending HTTP request {HttpMethod} {Uri}",
            request.Method,
            redacted);

        // No per-request context object needed — elapsed time is
        // supplied to LogRequestStop/Failed by the framework.
        return null;
    }

    /// <inheritdoc />
    public void LogRequestStop(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var redacted = TelegramHttpRedactor.Redact(request.RequestUri);
        _logger.LogInformation(
            "Received HTTP response {HttpMethod} {Uri} -> {StatusCode} after {ElapsedMilliseconds}ms",
            request.Method,
            redacted,
            (int)response.StatusCode,
            elapsed.TotalMilliseconds);
    }

    /// <inheritdoc />
    public void LogRequestFailed(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);

        var redacted = TelegramHttpRedactor.Redact(request.RequestUri);
        _logger.LogError(
            exception,
            "HTTP request failed {HttpMethod} {Uri} after {ElapsedMilliseconds}ms",
            request.Method,
            redacted,
            elapsed.TotalMilliseconds);
    }
}
