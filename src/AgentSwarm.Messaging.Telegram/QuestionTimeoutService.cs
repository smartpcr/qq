// -----------------------------------------------------------------------
// <copyright file="QuestionTimeoutService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Stage 3.5 — <see cref="BackgroundService"/> that periodically
/// polls <see cref="IPendingQuestionStore.GetExpiredAsync"/> and, for
/// each expired record, applies the denormalised default action (or
/// the <c>__timeout__</c> sentinel when no default was proposed),
/// edits the original Telegram message to indicate the timeout, and
/// records a <see cref="HumanResponseAuditEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architecture pin (§10.3).</b> The default-action emission
/// reads <see cref="PendingQuestion.DefaultActionId"/> directly from
/// the pending-question row and publishes that string verbatim as
/// the <see cref="HumanDecisionEvent.ActionValue"/>. The service
/// does NOT consult
/// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// — per architecture.md §10.3 and tech-spec D-3, the cache entries
/// the inline-keyboard build writes expire at
/// <see cref="AgentQuestion.ExpiresAt"/> (plus a small grace window)
/// and are likely evicted by the time the timeout fires. The
/// consuming agent resolves the full
/// <see cref="HumanAction"/> semantics from its own
/// <see cref="AgentQuestion.AllowedActions"/> list using the
/// <see cref="HumanAction.ActionId"/> value carried in
/// <see cref="HumanDecisionEvent.ActionValue"/>.
/// </para>
/// <para>
/// <b>Cross-process atomicity.</b> The polling query in
/// <see cref="IPendingQuestionStore.GetExpiredAsync"/> is a snapshot
/// — two worker instances polling the same store can both surface
/// the same row. To prevent a double-emission of
/// <see cref="HumanDecisionEvent"/> for the same
/// <see cref="HumanDecisionEvent.QuestionId"/>, the sweep first
/// calls <see cref="IPendingQuestionStore.MarkTimedOutAsync"/> as a
/// CAS-style atomic claim (translated to a single
/// <c>UPDATE … WHERE Status IN ('Pending','AwaitingComment')</c>
/// row-level conditional update). Only the caller whose UPDATE
/// matched a row proceeds to publish, edit, and audit; the losing
/// caller logs and returns. This eliminates the cross-process
/// double-publish race without depending on database-vendor
/// advisory locks.
/// </para>
/// <para>
/// <b>Sweep ordering.</b>
/// <list type="number">
///   <item><description><b>Atomic claim</b> via
///   <see cref="IPendingQuestionStore.MarkTimedOutAsync"/>. If the
///   call returns <see langword="false"/> (another worker / a
///   callback already terminated the row), skip the rest of the
///   steps for this row.</description></item>
///   <item><description>Publish
///   <see cref="ISwarmCommandBus.PublishHumanDecisionAsync"/> with
///   <see cref="HumanDecisionEvent.ActionValue"/> = the
///   <see cref="PendingQuestion.DefaultActionId"/> string verbatim
///   (or <c>"__timeout__"</c> when no default was proposed).</description></item>
///   <item><description>Edit the Telegram message (best-effort
///   cosmetic).</description></item>
///   <item><description>Write the audit entry (best-effort
///   observability).</description></item>
/// </list>
/// The trade is at-most-once-from-sweeper semantics: if the process
/// dies between the claim and the publish, the row is already
/// terminal and the next sweep will not retry. Recovery tooling can
/// query the audit table for <see cref="PendingQuestionStatus.TimedOut"/>
/// rows that lack a matching <see cref="HumanResponseAuditEntry"/>
/// for the <see cref="HumanResponseAuditEntry.QuestionId"/>. This is
/// preferable to the alternative (publish-first, claim-last) where
/// every retried sweep re-publishes the same decision and forces the
/// consuming agents to be unconditionally idempotent against
/// duplicates — see architecture.md §10.3.
/// </para>
/// <para>
/// <b>Failure handling.</b> A failure in ANY single record's
/// processing is logged and isolated — the loop continues with the
/// next expired record so a stuck row cannot block the entire
/// timeout pipeline. A failure of the polling query itself is also
/// caught and logged; the service waits one
/// <see cref="QuestionTimeoutOptions.PollInterval"/> and retries on
/// the next tick.
/// </para>
/// </remarks>
public sealed class QuestionTimeoutService : BackgroundService
{
    private const string TimeoutSentinelActionValue = "__timeout__";
    private const string TimeoutMessenger = "telegram";

    private readonly IPendingQuestionStore _pendingQuestionStore;
    private readonly ISwarmCommandBus _commandBus;
    private readonly IAuditLogger _auditLogger;
    private readonly ITelegramBotClient _botClient;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<QuestionTimeoutOptions> _options;
    private readonly ILogger<QuestionTimeoutService> _logger;

    public QuestionTimeoutService(
        IPendingQuestionStore pendingQuestionStore,
        ISwarmCommandBus commandBus,
        IAuditLogger auditLogger,
        ITelegramBotClient botClient,
        TimeProvider timeProvider,
        IOptions<QuestionTimeoutOptions> options,
        ILogger<QuestionTimeoutService> logger)
    {
        _pendingQuestionStore = pendingQuestionStore
            ?? throw new ArgumentNullException(nameof(pendingQuestionStore));
        _commandBus = commandBus ?? throw new ArgumentNullException(nameof(commandBus));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.PollInterval;
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromSeconds(30);
            _logger.LogWarning(
                "QuestionTimeoutOptions.PollInterval was non-positive; using default 30 seconds.");
        }

        _logger.LogInformation(
            "QuestionTimeoutService started; polling interval = {PollInterval}",
            interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "QuestionTimeoutService sweep failed; will retry after PollInterval.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("QuestionTimeoutService stopped.");
    }

    /// <summary>
    /// Internal single-sweep entrypoint, exposed for unit testing so
    /// the sweep body can be exercised without spinning up a hosted
    /// service loop. The production loop in <see cref="ExecuteAsync"/>
    /// invokes this method once per poll tick.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<PendingQuestion> expired;
        try
        {
            expired = await _pendingQuestionStore.GetExpiredAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "QuestionTimeoutService: GetExpiredAsync failed; nothing applied this sweep.");
            return;
        }

        if (expired.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "QuestionTimeoutService: found {Count} expired pending question(s); applying defaults.",
            expired.Count);

        foreach (var pending in expired)
        {
            try
            {
                await ApplyTimeoutAsync(pending, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "QuestionTimeoutService: failed to apply timeout to QuestionId={QuestionId} CorrelationId={CorrelationId}; isolating failure and continuing.",
                    pending.QuestionId,
                    pending.CorrelationId);
            }
        }
    }

    private async Task ApplyTimeoutAsync(PendingQuestion pending, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        // ----- Step 0: ATOMIC CLAIM. -----
        // Cross-process race guard. Two sweepers (two worker
        // instances, or this process + a stuck retry of itself) can
        // both surface the same row via GetExpiredAsync. The atomic
        // claim transitions Pending|AwaitingComment → TimedOut in a
        // single row-level UPDATE; only the winning caller sees
        // claimed=true and proceeds to publish. The loser sees
        // claimed=false and skips, preventing a double-emission of
        // HumanDecisionEvent for the same QuestionId (architecture.md
        // §10.3 — the consuming agent dedupes on QuestionId, but
        // closing the race at the source eliminates the dedupe burden
        // and avoids spurious audit-log duplicates).
        var claimed = await _pendingQuestionStore
            .MarkTimedOutAsync(pending.QuestionId, ct)
            .ConfigureAwait(false);
        if (!claimed)
        {
            _logger.LogDebug(
                "QuestionTimeoutService: lost the atomic claim for QuestionId={QuestionId} (another worker / a callback has already terminated this row); skipping publish.",
                pending.QuestionId);
            return;
        }

        // Per implementation-plan.md Stage 3.5 step 7 and architecture.md
        // §10.3 — the timeout publishes pending.DefaultActionId (a
        // string identifier) directly as the HumanDecisionEvent
        // ActionValue. The consuming agent resolves the full
        // HumanAction semantics from its own AllowedActions list; the
        // timeout service does NOT resolve HumanAction.Value, and does
        // NOT consult IDistributedCache (whose entries expire at
        // AgentQuestion.ExpiresAt per tech-spec D-3 and may already be
        // evicted by the time this sweep fires).
        var actionValue = pending.DefaultActionId ?? TimeoutSentinelActionValue;

        // ----- Step 1: publish the HumanDecisionEvent. -----
        // Performed AFTER the atomic claim so the cross-process race
        // is closed before any side-effect runs. If publish throws
        // here, the row is already TimedOut and the next sweep will
        // skip it; the resulting "claimed but never published" gap is
        // recoverable by querying the audit table for TimedOut rows
        // with no corresponding HumanResponseAuditEntry. This is the
        // intentional trade for at-most-once-from-sweeper semantics:
        // duplicate publishes are worse than rare gaps because the
        // consuming agents are not unconditionally idempotent against
        // multiple HumanDecisionEvents for the same QuestionId.
        var decision = new HumanDecisionEvent
        {
            QuestionId = pending.QuestionId,
            ActionValue = actionValue,
            Comment = null,
            Messenger = TimeoutMessenger,
            // No real operator user — represent the system-driven
            // timeout with the same sentinel used in ActionValue.
            ExternalUserId = TimeoutSentinelActionValue,
            // Reuse the original Telegram message id so audit /
            // correlation tooling can tie the decision back to the
            // pending row.
            ExternalMessageId = pending.TelegramMessageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ReceivedAt = now,
            CorrelationId = pending.CorrelationId,
        };
        await _commandBus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);

        // ----- Step 2: edit the Telegram message (best-effort). -----
        await EditTelegramMessageAsync(pending, ct).ConfigureAwait(false);

        // ----- Step 3: write the audit entry (best-effort). -----
        await WriteAuditEntryAsync(pending, decision, now, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "QuestionTimeoutService applied default action. QuestionId={QuestionId} ActionValue={ActionValue} CorrelationId={CorrelationId}",
            pending.QuestionId,
            actionValue,
            pending.CorrelationId);
    }

    private async Task EditTelegramMessageAsync(
        PendingQuestion pending,
        CancellationToken ct)
    {
        // Telegram's MessageId field is int32 but PendingQuestion
        // stores the value as long for forward-compat. Out-of-range
        // ids are not editable via the Bot API; log + skip — the
        // decision-event step has already succeeded.
        if (pending.TelegramMessageId < int.MinValue
            || pending.TelegramMessageId > int.MaxValue)
        {
            _logger.LogWarning(
                "QuestionTimeoutService: skipping Telegram edit, message id {TelegramMessageId} does not fit int32. QuestionId={QuestionId}",
                pending.TelegramMessageId,
                pending.QuestionId);
            return;
        }

        var text = BuildTimeoutMessageText(pending);
        try
        {
            await _botClient.SendRequest(
                    new EditMessageTextRequest
                    {
                        ChatId = pending.TelegramChatId,
                        MessageId = (int)pending.TelegramMessageId,
                        Text = text,
                        // Plain text — user-supplied Title in the
                        // original render could re-fire MarkdownV2
                        // escape rules and reject the edit; the
                        // CallbackQueryHandler's decision edit uses
                        // the same ParseMode.None for the same reason.
                        ParseMode = ParseMode.None,
                        // ReplyMarkup intentionally null — drops the
                        // inline keyboard so a late tap after the
                        // timeout cannot fire another action.
                        ReplyMarkup = null,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (
            ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase))
        {
            // Benign: a prior crashed sweep already edited the
            // message, or the operator deleted the chat. No-op so we
            // can still proceed to MarkTimedOut.
            _logger.LogDebug(
                ex,
                "QuestionTimeoutService: Telegram edit was a no-op (message gone or unchanged). QuestionId={QuestionId}",
                pending.QuestionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cosmetic enhancement — log + swallow so the sweep can
            // still mark the row TimedOut and the decision event
            // (already published) is not undone by a Telegram outage.
            _logger.LogWarning(
                ex,
                "QuestionTimeoutService: failed to edit Telegram message. QuestionId={QuestionId} ChatId={ChatId} MessageId={MessageId}",
                pending.QuestionId,
                pending.TelegramChatId,
                pending.TelegramMessageId);
        }
    }

    /// <summary>
    /// Builds the plain-text edit body the timeout sweep posts back
    /// to Telegram. Per the workstream brief's test scenarios:
    /// <list type="bullet">
    ///   <item><description><c>"⏰ Timed out — default action applied: {DefaultActionId}"</c>
    ///   when the question had a default action. The
    ///   <see cref="PendingQuestion.DefaultActionId"/> string is used
    ///   verbatim (NOT the action label) so the rendered text matches
    ///   the workstream scenario "DefaultActionId=skip → '⏰ Timed
    ///   out — default action applied: skip'" exactly.</description></item>
    ///   <item><description><c>"⏰ Timed out — no default action"</c>
    ///   when no default was proposed.</description></item>
    /// </list>
    /// </summary>
    internal static string BuildTimeoutMessageText(PendingQuestion pending)
    {
        if (pending.DefaultActionId is not null)
        {
            return $"⏰ Timed out — default action applied: {pending.DefaultActionId}";
        }

        return "⏰ Timed out — no default action";
    }

    private async Task WriteAuditEntryAsync(
        PendingQuestion pending,
        HumanDecisionEvent decision,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await _auditLogger
                .LogHumanResponseAsync(
                    new HumanResponseAuditEntry
                    {
                        EntryId = Guid.NewGuid(),
                        MessageId = decision.ExternalMessageId,
                        UserId = decision.ExternalUserId,
                        AgentId = pending.AgentId,
                        QuestionId = pending.QuestionId,
                        ActionValue = decision.ActionValue,
                        Comment = null,
                        Timestamp = now,
                        CorrelationId = pending.CorrelationId,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "QuestionTimeoutService: failed to write audit entry; HumanDecisionEvent has already been published so the decision is durable. QuestionId={QuestionId} CorrelationId={CorrelationId}",
                pending.QuestionId,
                pending.CorrelationId);
        }
    }
}
