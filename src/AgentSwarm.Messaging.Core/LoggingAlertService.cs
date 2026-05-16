// -----------------------------------------------------------------------
// <copyright file="LoggingAlertService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAlertService"/> implementation that emits a
/// structured <c>LogCritical</c> for every dead-letter / critical-
/// failure event. Registered by the production Worker host as the
/// fallback for the "Telegram sender exhausts retries" path so the
/// alert is at minimum visible in the host's log sink even before an
/// out-of-band channel (Slack, PagerDuty, second-bot) is wired up
/// in a later stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Iter-4 evaluator item 6.</b> Earlier iterations registered
/// <see cref="IAlertService"/> as optional in the sender's ctor but
/// did NOT supply a production default; that left the dead-letter
/// alert path effectively log-only via the sender's own
/// <see cref="ILogger"/> only when no <see cref="IAlertService"/>
/// was resolved. Registering this concrete in the Worker means the
/// sender ALWAYS has an <see cref="IAlertService"/> in hand and the
/// dead-letter flow goes through the dedicated alert sink — a single
/// chokepoint the future Slack / PagerDuty implementation can swap
/// out via <c>Replace</c> without touching the sender.
/// </para>
/// <para>
/// <b>Avoiding the "alert about Telegram via Telegram" loop.</b>
/// This impl deliberately logs only — it does NOT enqueue another
/// outbound Telegram message that could itself fail and re-trigger
/// the alert. Out-of-band channels (Slack, email) will replace this
/// registration in their respective wiring stages.
/// </para>
/// </remarks>
public sealed class LoggingAlertService : IAlertService
{
    private readonly ILogger<LoggingAlertService> _logger;

    public LoggingAlertService(ILogger<LoggingAlertService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task SendAlertAsync(string subject, string detail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(detail);

        // LogCritical so the alert surfaces in any sane production
        // log routing (cloud log analytics, journald, Serilog sinks).
        // Structured properties (Subject, Detail) keep the log line
        // machine-queryable so an operator log search for
        // "dead-letter" returns every alert subject and the responder
        // can pivot directly into the trace correlation id embedded
        // in Detail.
        _logger.LogCritical(
            "ALERT: {Subject}. {Detail}",
            subject,
            detail);
        return Task.CompletedTask;
    }
}
