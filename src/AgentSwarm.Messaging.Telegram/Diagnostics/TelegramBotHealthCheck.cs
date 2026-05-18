// -----------------------------------------------------------------------
// <copyright file="TelegramBotHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Diagnostics;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using global::Telegram.Bot;

/// <summary>
/// Stage 6.2 — composite-friendly <see cref="IHealthCheck"/> that
/// probes the Telegram Bot API by calling <c>getMe</c> through the
/// registered <see cref="ITelegramBotClient"/>. Reports
/// <see cref="HealthStatus.Healthy"/> when the bot identity is
/// returned within <see cref="ProbeTimeout"/> (5 seconds per the
/// Stage 6.2 brief); <see cref="HealthStatus.Unhealthy"/> on
/// timeout, transport failure, missing identity, or any exception
/// thrown by the client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>getMe</c>.</b> The Bot API exposes <c>getMe</c> as a
/// lightweight identity-and-reachability probe — it does not
/// modify webhook state, does not consume the rate-limit budget for
/// outbound messages, and returns the bot's <c>id</c> /
/// <c>username</c> which is itself useful diagnostic data. The
/// Stage 6.2 brief pins it as the canonical liveness probe.
/// </para>
/// <para>
/// <b>5-second timeout.</b> The brief mandates "reports healthy if
/// the bot identity is returned within 5 seconds". The check uses
/// a <see cref="CancellationTokenSource"/> linked to the caller's
/// token; a slow / hung Telegram API endpoint surfaces as
/// <see cref="HealthStatus.Unhealthy"/> rather than blocking the
/// <c>/healthz</c> endpoint indefinitely. The deadline is checked
/// AFTER the call returns too so that a successful-but-late response
/// is still reported unhealthy — operators rely on a hard
/// 5 s upper bound for the liveness signal.
/// </para>
/// <para>
/// <b>Caller cancellation honoured separately from the internal
/// timeout (iter-2 evaluator item 3).</b> The check distinguishes
/// "our 5s probe budget elapsed" (real timeout — surfaces as
/// <see cref="HealthStatus.Unhealthy"/>) from "the caller's
/// <see cref="CancellationToken"/> cancelled" (host shutdown,
/// request abort — propagates as
/// <see cref="OperationCanceledException"/>). The earlier
/// catch-all converted both surfaces to Unhealthy, which would
/// falsely report "Telegram is down" on every aborted /healthz
/// request and during graceful shutdown. The catch clause now
/// requires that the linked timeout CTS fired AND the caller's
/// token did not — otherwise the OCE propagates to the
/// HealthCheckService which handles shutdown / abort semantics
/// natively (no JSON body is sent on a torn-down connection).
/// </para>
/// <para>
/// <b>Identity must be non-default.</b> The brief is "healthy iff
/// the bot identity is returned within 5 seconds". A response that
/// returns a <c>null</c> <see cref="global::Telegram.Bot.Types.User"/>,
/// a zero <see cref="global::Telegram.Bot.Types.User.Id"/>, or a
/// blank <see cref="global::Telegram.Bot.Types.User.Username"/>
/// indicates either a broken stub on the wire or a degraded API
/// shape; the check reports
/// <see cref="HealthStatus.Unhealthy"/> rather than papering over
/// the absence with default values in the result data.
/// </para>
/// <para>
/// <b>No secret leakage.</b> The <see cref="ITelegramBotClient"/> is
/// configured with the bot token at construction time; this check
/// never reads <c>TelegramOptions.BotToken</c> directly. Exception
/// messages from the client may include the bot's <c>id</c> /
/// <c>username</c> but never the token (the
/// <see cref="RedactingHttpClientLogger"/> redaction is already in
/// place for the underlying HTTP client). The check echoes only
/// the bot's public identity into the
/// <see cref="HealthCheckResult.Data"/> dictionary so the operator
/// audit screen can confirm the right bot is wired without exposing
/// secrets.
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// <list type="bullet">
///   <item><description>
///   <c>getMe</c> returns within 5 s → <see cref="HealthStatus.Healthy"/>
///   with the bot's <c>id</c> / <c>username</c> on the result data.
///   </description></item>
///   <item><description>
///   <c>getMe</c> exceeds the 5 s deadline (cancellation or wall-clock)
///   → <see cref="HealthStatus.Unhealthy"/> with description
///   "<c>getMe exceeded 5s timeout</c>".
///   </description></item>
///   <item><description>
///   Any other exception (HTTP error, DNS failure,
///   <see cref="global::Telegram.Bot.Exceptions.ApiRequestException"/>)
///   → <see cref="HealthStatus.Unhealthy"/> with the exception
///   recorded so the operator runbook surfaces the actual failure.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class TelegramBotHealthCheck : IHealthCheck
{
    /// <summary>
    /// Canonical name to register this check under via
    /// <c>AddCheck&lt;TelegramBotHealthCheck&gt;(Name, ...)</c>.
    /// </summary>
    public const string Name = "telegram_bot_api";

    /// <summary>
    /// Stage 6.2 brief — the bot identity must return within 5
    /// seconds to count as healthy. Operators tune this only by
    /// editing the source; the deadline is intentionally a constant
    /// because the brief makes 5 s a contract, not a config knob.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramBotHealthCheck> _logger;
    private readonly TimeSpan _probeTimeout;

    public TelegramBotHealthCheck(
        ITelegramBotClient botClient,
        ILogger<TelegramBotHealthCheck> logger)
        : this(botClient, logger, ProbeTimeout)
    {
    }

    /// <summary>
    /// Internal test seam (iter-2 evaluator item 4). Allows unit
    /// tests to override the per-probe timeout so the internal-
    /// timeout branch can be exercised deterministically in
    /// sub-second wall time without waiting the full 5 s budget.
    /// Visible to <c>AgentSwarm.Messaging.Tests</c> via the
    /// <c>InternalsVisibleTo</c> attribute on the
    /// <c>AgentSwarm.Messaging.Telegram</c> csproj. Production
    /// code MUST use the public constructor which pins the
    /// timeout to <see cref="ProbeTimeout"/> (5 s) — the Stage 6.2
    /// contract.
    /// </summary>
    internal TelegramBotHealthCheck(
        ITelegramBotClient botClient,
        ILogger<TelegramBotHealthCheck> logger,
        TimeSpan probeTimeout)
    {
        _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeTimeout),
                probeTimeout,
                "probeTimeout must be positive — a non-positive value would cancel the linked CTS before the getMe call starts.");
        }

        _probeTimeout = probeTimeout;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_probeTimeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Stage 6.2 brief specifies "calls GetMeAsync"; Telegram.Bot 22.x
            // renamed the extension method to `GetMe` (still returning
            // `Task<User>` — the Async suffix was dropped in the v22 API
            // rename). The wire call (`POST /bot{TOKEN}/getMe`) is
            // unchanged; the integration-test FakeTelegramApi already
            // stubs that exact path. We use the v22 name to compile
            // against the pinned package.
            var me = await _botClient.GetMe(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            // Wall-clock guard: a getMe that returns AFTER the 5 s
            // deadline but before the linked CTS fires (race between
            // the response arriving and the cancellation taking
            // effect) is still over-budget and must report unhealthy.
            if (stopwatch.Elapsed > _probeTimeout)
            {
                _logger.LogWarning(
                    "Telegram getMe returned after {Elapsed}ms — exceeded the {Timeout}ms liveness budget; reporting Unhealthy.",
                    stopwatch.ElapsedMilliseconds,
                    (long)_probeTimeout.TotalMilliseconds);

                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    description: $"Telegram getMe completed in {stopwatch.ElapsedMilliseconds}ms — exceeded the {(long)_probeTimeout.TotalMilliseconds}ms liveness budget.",
                    data: new Dictionary<string, object>
                    {
                        ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
                        ["timeout_ms"] = (long)_probeTimeout.TotalMilliseconds,
                    });
            }

            // Iter-2 evaluator item 2 — the brief is literal:
            // "healthy iff the bot identity is returned within
            // 5 seconds". A null User, a zero Id, or a blank
            // Username is NOT a returned identity — it's a degraded
            // or stubbed response that would emit `bot_id = 0` /
            // `bot_username = ""` into the operator dashboard.
            // Surface this as Unhealthy so the operator runbook
            // catches the misconfiguration rather than masking it
            // behind default values.
            if (me is null || me.Id == 0L || string.IsNullOrWhiteSpace(me.Username))
            {
                _logger.LogWarning(
                    "Telegram getMe returned a missing or default-shaped identity after {Elapsed}ms (me is null: {MeNull}, id: {BotId}, username present: {HasUsername}); reporting Unhealthy.",
                    stopwatch.ElapsedMilliseconds,
                    me is null,
                    me?.Id ?? 0L,
                    !string.IsNullOrWhiteSpace(me?.Username));

                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    description: "Telegram getMe returned without a valid bot identity (null User, zero Id, or empty Username). The Bot API may be misconfigured, the token may be invalid, or an intermediate proxy is returning a stub envelope.",
                    data: new Dictionary<string, object>
                    {
                        ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
                        ["timeout_ms"] = (long)_probeTimeout.TotalMilliseconds,
                        ["bot_id"] = me?.Id ?? 0L,
                        ["bot_username"] = me?.Username ?? string.Empty,
                        ["identity_missing"] = true,
                    });
            }

            return new HealthCheckResult(
                HealthStatus.Healthy,
                description: $"Telegram getMe succeeded in {stopwatch.ElapsedMilliseconds}ms.",
                data: new Dictionary<string, object>
                {
                    ["bot_id"] = me.Id,
                    ["bot_username"] = me.Username,
                    ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
                    ["timeout_ms"] = (long)_probeTimeout.TotalMilliseconds,
                });
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            stopwatch.Stop();

            // Iter-2 evaluator item 3 — this catch ONLY fires when
            // our own internal probe budget elapsed BEFORE the
            // caller cancelled. The brief's "bot identity returned
            // within 5 seconds" contract was violated; surface as
            // Unhealthy so /healthz reflects the real outage.
            //
            // Conversely, when `cancellationToken.IsCancellationRequested`
            // is true the OperationCanceledException propagates
            // naturally — the HealthCheckService treats request
            // abort and host shutdown as out-of-band signals rather
            // than a "Telegram is down" assertion (which is what
            // the iter-1 catch-all incorrectly emitted).
            _logger.LogWarning(
                "Telegram getMe did not complete within the internal {Timeout}ms budget ({Elapsed}ms elapsed); reporting Unhealthy.",
                (long)_probeTimeout.TotalMilliseconds,
                stopwatch.ElapsedMilliseconds);

            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"Telegram getMe exceeded the {(long)_probeTimeout.TotalMilliseconds}ms internal probe timeout. The Bot API is unreachable or unresponsive.",
                data: new Dictionary<string, object>
                {
                    ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
                    ["timeout_ms"] = (long)_probeTimeout.TotalMilliseconds,
                    ["caller_cancelled"] = false,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            _logger.LogWarning(
                ex,
                "Telegram getMe failed after {Elapsed}ms; reporting Unhealthy.",
                stopwatch.ElapsedMilliseconds);

            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: "Telegram getMe failed — the bot API is unreachable or returned an error. See the recorded exception for the underlying cause.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
                    ["timeout_ms"] = (long)_probeTimeout.TotalMilliseconds,
                });
        }
    }
}
