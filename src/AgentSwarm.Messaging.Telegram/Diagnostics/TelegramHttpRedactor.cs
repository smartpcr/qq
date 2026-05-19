// -----------------------------------------------------------------------
// <copyright file="TelegramHttpRedactor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Diagnostics;

using System;
using System.Text.RegularExpressions;

/// <summary>
/// Stage 6.1 — pure-function URL redactor used by the OpenTelemetry
/// HTTP-client instrumentation hook to strip the Telegram bot token
/// from outbound URLs BEFORE they reach a span attribute.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The Telegram Bot SDK posts to
/// <c>https://api.telegram.org/bot{TOKEN}/sendMessage</c> (and the
/// equivalent path for every other Bot API method). The token is
/// the bearer credential — anyone holding it can impersonate the
/// bot — so it MUST NOT land on a span attribute
/// (<c>http.url</c> / <c>url.full</c>) where an OTLP exporter would
/// ship it to the observability backend.
/// </para>
/// <para>
/// <b>Defence in depth.</b> The production <see cref="TelegramOptions"/>
/// already overrides <see cref="object.ToString"/> to redact the token
/// (<see cref="TelegramOptions.ToString"/>), and the
/// <c>TelegramSecretSourceValidator</c> startup hook never logs the
/// value. This redactor closes the OTel-instrumentation gap: the
/// <c>HttpClient</c> the SDK uses is wired through the framework's
/// <c>IHttpClientFactory</c>, and the OTEL instrumentation captures
/// raw URLs by default.
/// </para>
/// </remarks>
public static class TelegramHttpRedactor
{
    /// <summary>
    /// Placeholder substituted for the bot token in the redacted URL.
    /// Matches the marker convention used by
    /// <see cref="TelegramOptions.ToString"/>.
    /// </summary>
    public const string TokenRedaction = "***REDACTED***";

    // Telegram bot tokens follow the documented shape
    // `<bot_id>:<35-char-alphanumeric-with-underscore-hyphen>`,
    // e.g. `111111:integration-test-bot-token` (from the integration
    // suite) or `5234567890:AAFmJgKZ-AhJfFoo_examplebotApiToken`
    // (production-shaped). The regex below matches the segment
    // beginning with `/bot` and ending at the next `/`, capturing
    // everything between so the redaction works for both real
    // production tokens AND the synthetic short tokens unit tests
    // use. The path family also includes `/botTOKEN/setWebhook`,
    // `/botTOKEN/sendMessage`, `/botTOKEN/getMe`, ...
    private static readonly Regex BotTokenPathPattern = new(
        @"/bot(?<token>[^/]+)/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns <paramref name="url"/> with any embedded Telegram bot
    /// token replaced by <see cref="TokenRedaction"/>. Inputs that
    /// do not match the <c>/bot{TOKEN}/</c> path family are returned
    /// unchanged.
    /// </summary>
    public static string Redact(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        return BotTokenPathPattern.Replace(url, "/bot" + TokenRedaction + "/");
    }

    /// <summary>
    /// Convenience overload for <see cref="Uri"/> values; returns the
    /// redacted absolute URI string.
    /// </summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        return Redact(uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.ToString());
    }
}
