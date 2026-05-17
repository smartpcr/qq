using System.Globalization;
using System.Linq;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Result returned by <see cref="InstallationStateGate.CheckAsync"/>.
/// </summary>
/// <param name="IsActive">
/// <c>true</c> when the target reference exists and is active (proactive send may proceed);
/// <c>false</c> when the gate has rejected the send (the message has already been
/// dead-lettered and audited).
/// </param>
/// <param name="Reason">
/// Human-readable explanation of why the gate decided as it did. Always populated.
/// </param>
public sealed record InstallationStateGateResult(bool IsActive, string Reason);

/// <summary>
/// Pre-send gate that enforces the Teams-app installation prerequisite for proactive
/// messaging. Aligned with <c>tech-spec.md</c> §4.2 (installation gate) and
/// <c>implementation-plan.md</c> §5.1 step 6.
/// </summary>
/// <remarks>
/// <para>
/// The gate exists because <c>BotAdapter.ContinueConversationAsync</c> requires an active
/// installation; calling it for a user who uninstalled the bot fails with HTTP 403/404 and
/// pollutes the retry queue. By checking
/// <see cref="IConversationReferenceStore.IsActiveByInternalUserIdAsync"/> /
/// <see cref="IConversationReferenceStore.IsActiveByChannelAsync"/> first, the gate
/// short-circuits known-uninstalled targets without a wasted Bot Framework round-trip.
/// </para>
/// <para>
/// Use the direct <c>IsActiveBy*Async</c> probes instead of <c>GetBy*Async</c> followed by
/// a null-check: the getter returns only active rows, so it cannot disambiguate
/// "never installed" from "uninstalled". The dedicated probes distinguish the two cases
/// and the audit payload records which path triggered the rejection.
/// </para>
/// <para>
/// <b>Reject behaviour</b>: dead-letters the outbox entry via
/// <see cref="IMessageOutbox.DeadLetterAsync"/> AND emits an audit entry of
/// <see cref="AuditEventTypes.Error"/> + <see cref="AuditOutcomes.Failed"/>, both per the
/// implementation-plan §5.1 step 6 contract.
/// </para>
/// </remarks>
public sealed class InstallationStateGate
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IConversationReferenceStore _referenceStore;
    private readonly IMessageOutbox _outbox;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<InstallationStateGate> _logger;

    /// <summary>Construct an <see cref="InstallationStateGate"/>.</summary>
    /// <exception cref="ArgumentNullException">If any dependency is null.</exception>
    public InstallationStateGate(
        IConversationReferenceStore referenceStore,
        IMessageOutbox outbox,
        IAuditLogger auditLogger,
        ILogger<InstallationStateGate> logger)
    {
        _referenceStore = referenceStore ?? throw new ArgumentNullException(nameof(referenceStore));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Check whether the conversation reference targeted by <paramref name="question"/> is
    /// still active. When the gate rejects, it has already dead-lettered the outbox entry
    /// (when <paramref name="outboxEntryId"/> is non-null) and emitted the audit row — the
    /// caller must NOT retry the Bot Framework send.
    /// </summary>
    /// <param name="question">The agent question whose target is being evaluated.</param>
    /// <param name="outboxEntryId">
    /// The outbox entry ID associated with the pending send; supplied so a rejection can
    /// dead-letter the entry. Pass <c>null</c> when the gate is called from a direct
    /// (non-outbox) send path — the gate then skips the <c>IMessageOutbox.DeadLetterAsync</c>
    /// call but still emits the <c>InstallationGateRejected</c> audit entry so compliance
    /// review records every install-state denial regardless of caller.
    /// </param>
    /// <param name="correlationId">Trace correlation ID inherited from the question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="InstallationStateGateResult"/> describing the decision.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="question"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="question"/> does not specify a target user or channel.</exception>
    public async Task<InstallationStateGateResult> CheckAsync(
        AgentQuestion question,
        string? outboxEntryId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (question is null) throw new ArgumentNullException(nameof(question));

        var hasUser = !string.IsNullOrEmpty(question.TargetUserId);
        var hasChannel = !string.IsNullOrEmpty(question.TargetChannelId);
        if (!hasUser && !hasChannel)
        {
            throw new ArgumentException(
                $"AgentQuestion '{question.QuestionId}' specifies neither TargetUserId nor TargetChannelId; cannot route.",
                nameof(question));
        }

        if (hasUser)
        {
            var active = await _referenceStore
                .IsActiveByInternalUserIdAsync(question.TenantId, question.TargetUserId!, cancellationToken)
                .ConfigureAwait(false);

            if (active)
            {
                return new InstallationStateGateResult(true, "Active user-scoped conversation reference.");
            }

            var reason = $"User-scoped conversation reference for internal user '{question.TargetUserId}' in tenant '{question.TenantId}' is missing or inactive.";
            await RejectAsync(
                question: question,
                outboxEntryId: outboxEntryId,
                correlationId: correlationId,
                scope: "User",
                target: question.TargetUserId!,
                reason: reason,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new InstallationStateGateResult(false, reason);
        }

        var channelActive = await _referenceStore
            .IsActiveByChannelAsync(question.TenantId, question.TargetChannelId!, cancellationToken)
            .ConfigureAwait(false);

        if (channelActive)
        {
            return new InstallationStateGateResult(true, "Active channel-scoped conversation reference.");
        }

        var channelReason = $"Channel-scoped conversation reference for channel '{question.TargetChannelId}' in tenant '{question.TenantId}' is missing or inactive.";
        await RejectAsync(
            question: question,
            outboxEntryId: outboxEntryId,
            correlationId: correlationId,
            scope: "Channel",
            target: question.TargetChannelId!,
            reason: channelReason,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new InstallationStateGateResult(false, channelReason);
    }

    /// <summary>
    /// Tenant + target probe used by the non-question proactive paths
    /// (<see cref="AgentSwarm.Messaging.Abstractions.IProactiveNotifier.SendProactiveAsync"/> /
    /// <see cref="AgentSwarm.Messaging.Abstractions.IProactiveNotifier.SendToChannelAsync"/>)
    /// which carry a <c>MessengerMessage</c> rather than an
    /// <see cref="AgentQuestion"/>. Exactly one of <paramref name="userId"/> or
    /// <paramref name="channelId"/> MUST be supplied. The rejection path emits the same
    /// <c>InstallationGateRejected</c> audit row as the question overload and dead-letters
    /// the outbox entry when <paramref name="outboxEntryId"/> is non-null.
    /// </summary>
    /// <param name="tenantId">Entra tenant ID owning the target.</param>
    /// <param name="userId">Internal user ID for user-scoped sends; mutually exclusive with <paramref name="channelId"/>.</param>
    /// <param name="channelId">Teams channel ID for channel-scoped sends; mutually exclusive with <paramref name="userId"/>.</param>
    /// <param name="correlationId">Trace correlation ID inherited from the outbox / caller.</param>
    /// <param name="outboxEntryId">Outbox entry ID to dead-letter on rejection; pass <c>null</c> for non-outbox callers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="InstallationStateGateResult"/> describing the decision.</returns>
    /// <exception cref="ArgumentException">If <paramref name="tenantId"/> is null/empty or BOTH (or NEITHER) of <paramref name="userId"/> / <paramref name="channelId"/> is supplied.</exception>
    /// <exception cref="InstallationStateGateComplianceException">If the audit logger or outbox dead-letter could not record compliance evidence — gate fails closed.</exception>
    public async Task<InstallationStateGateResult> CheckTargetAsync(
        string tenantId,
        string? userId,
        string? channelId,
        string correlationId,
        string? outboxEntryId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        }

        var hasUser = !string.IsNullOrEmpty(userId);
        var hasChannel = !string.IsNullOrEmpty(channelId);
        if (hasUser == hasChannel)
        {
            throw new ArgumentException(
                $"InstallationStateGate.CheckTargetAsync requires exactly one of userId or channelId; got userId={(hasUser ? "set" : "null")} channelId={(hasChannel ? "set" : "null")}.",
                hasUser ? nameof(channelId) : nameof(userId));
        }

        // Synthesise the minimal AgentQuestion needed by the shared RejectAsync helper so
        // the audit payload / dead-letter shape is identical regardless of whether the
        // gate was invoked with a real AgentQuestion or a bare MessengerMessage target.
        // Marker QuestionId / AgentId ("system") makes message-target rejections
        // distinguishable from real question rejections in the audit log. This
        // synthetic record is never persisted or returned to the caller — RejectAsync
        // only reads QuestionId / TenantId / AgentId / TaskId / CorrelationId from it.
        var safeCorrelationId = string.IsNullOrEmpty(correlationId)
            ? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)
            : correlationId;
        var syntheticQuestion = new AgentQuestion
        {
            QuestionId = $"messenger-message::{Guid.NewGuid():D}",
            CorrelationId = safeCorrelationId,
            AgentId = "system",
            TaskId = "messenger-message",
            TenantId = tenantId,
            TargetUserId = userId,
            TargetChannelId = channelId,
            Title = "MessengerMessage proactive send (gate probe only)",
            Body = "InstallationStateGate.CheckTargetAsync synthetic placeholder — not persisted.",
            Severity = MessageSeverities.Info,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };

        if (hasUser)
        {
            var active = await _referenceStore
                .IsActiveByInternalUserIdAsync(tenantId, userId!, cancellationToken)
                .ConfigureAwait(false);

            if (active)
            {
                return new InstallationStateGateResult(true, "Active user-scoped conversation reference.");
            }

            var reason = $"User-scoped conversation reference for internal user '{userId}' in tenant '{tenantId}' is missing or inactive.";
            await RejectAsync(
                question: syntheticQuestion,
                outboxEntryId: outboxEntryId,
                correlationId: correlationId,
                scope: "User",
                target: userId!,
                reason: reason,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new InstallationStateGateResult(false, reason);
        }

        var channelActive = await _referenceStore
            .IsActiveByChannelAsync(tenantId, channelId!, cancellationToken)
            .ConfigureAwait(false);

        if (channelActive)
        {
            return new InstallationStateGateResult(true, "Active channel-scoped conversation reference.");
        }

        var channelReason = $"Channel-scoped conversation reference for channel '{channelId}' in tenant '{tenantId}' is missing or inactive.";
        await RejectAsync(
            question: syntheticQuestion,
            outboxEntryId: outboxEntryId,
            correlationId: correlationId,
            scope: "Channel",
            target: channelId!,
            reason: channelReason,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new InstallationStateGateResult(false, channelReason);
    }

    /// <summary>
    /// Conversation-ID rejection helper used by the bare-MessengerMessage routing path
    /// (<see cref="AgentSwarm.Messaging.Abstractions.IMessengerConnector.SendMessageAsync"/>).
    /// Unlike the user/channel paths, the connector cannot probe install-state before the
    /// lookup because <see cref="MessengerMessage"/> carries only a Bot Framework
    /// <c>ConversationId</c> (no tenant / user / channel identity); the canonical store's
    /// <c>GetByConversationIdAsync</c> already filters by <c>IsActive</c>, so a null result
    /// definitively means "no active reference for this conversation" — which is the
    /// install-state rejection condition. This method emits the same
    /// <c>InstallationGateRejected</c> audit row + dead-letters the outbox entry that the
    /// CheckAsync / CheckTargetAsync paths emit, so the connector's rejection path produces
    /// identical compliance evidence regardless of how the original send was routed.
    /// </summary>
    /// <param name="message">The outbound message whose routing failed.</param>
    /// <param name="conversationId">The Bot Framework conversation ID that yielded no active reference.</param>
    /// <param name="outboxEntryId">Outbox entry ID to dead-letter on rejection; pass <c>null</c> for non-outbox callers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rejection reason string (so the caller can include it in the thrown exception's message).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="conversationId"/> is null or empty.</exception>
    /// <exception cref="InstallationStateGateComplianceException">If the audit logger or outbox dead-letter could not record compliance evidence — gate fails closed.</exception>
    public async Task<string> RejectMessageRoutingAsync(
        MessengerMessage message,
        string conversationId,
        string? outboxEntryId,
        CancellationToken cancellationToken)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (string.IsNullOrEmpty(conversationId))
        {
            throw new ArgumentException("Conversation ID is required.", nameof(conversationId));
        }

        // Synthesise a minimal AgentQuestion carrying the MessengerMessage's traceability
        // fields so the InstallationGateRejected audit row records the originating
        // correlation / agent / task identifiers (same shape that CheckAsync and
        // CheckTargetAsync produce). Marker QuestionId "messenger-message-routing::*"
        // makes ConversationId-routed rejections distinguishable from user/channel
        // rejections in the audit log.
        var safeCorrelationId = string.IsNullOrEmpty(message.CorrelationId)
            ? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)
            : message.CorrelationId;
        var syntheticQuestion = new AgentQuestion
        {
            QuestionId = $"messenger-message-routing::{message.MessageId}",
            CorrelationId = safeCorrelationId,
            AgentId = string.IsNullOrEmpty(message.AgentId) ? "system" : message.AgentId,
            TaskId = string.IsNullOrEmpty(message.TaskId) ? "messenger-message-routing" : message.TaskId,
            TenantId = string.Empty,
            ConversationId = conversationId,
            Title = "MessengerMessage routing (gate probe only)",
            Body = "InstallationStateGate.RejectMessageRoutingAsync synthetic placeholder — not persisted.",
            Severity = MessageSeverities.Info,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };

        var reason =
            $"No active TeamsConversationReference for conversation '{conversationId}' (message '{message.MessageId}'). " +
            "The Teams app must be installed and a prior interaction must have captured a reference before proactive delivery can succeed.";

        await RejectAsync(
            question: syntheticQuestion,
            outboxEntryId: outboxEntryId,
            correlationId: safeCorrelationId,
            scope: "Conversation",
            target: conversationId,
            reason: reason,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return reason;
    }

    private async Task RejectAsync(
        AgentQuestion question,
        string? outboxEntryId,
        string correlationId,
        string scope,
        string target,
        string reason,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "InstallationStateGate rejected {Scope}-scoped proactive send for question {QuestionId} (tenant {TenantId}, target {Target}). Reason: {Reason}",
            scope,
            question.QuestionId,
            question.TenantId,
            target,
            reason);

        var payload = JsonSerializer.Serialize(
            new
            {
                questionId = question.QuestionId,
                outboxEntryId,
                scope,
                target,
                tenantId = question.TenantId,
                reason,
            },
            PayloadJsonOptions);

        var timestamp = DateTimeOffset.UtcNow;
        var actorId = string.IsNullOrEmpty(question.AgentId) ? "system" : question.AgentId;
        var safeCorrelationId = string.IsNullOrEmpty(correlationId)
            ? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)
            : correlationId;
        var entry = new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = safeCorrelationId,
            EventType = AuditEventTypes.Error,
            ActorId = actorId,
            ActorType = AuditActorTypes.Agent,
            TenantId = question.TenantId,
            AgentId = question.AgentId,
            TaskId = question.TaskId,
            ConversationId = null,
            Action = "InstallationGateRejected",
            PayloadJson = payload,
            Outcome = AuditOutcomes.Failed,
            Checksum = AuditEntry.ComputeChecksum(
                timestamp: timestamp,
                correlationId: safeCorrelationId,
                eventType: AuditEventTypes.Error,
                actorId: actorId,
                actorType: AuditActorTypes.Agent,
                tenantId: question.TenantId,
                agentId: question.AgentId,
                taskId: question.TaskId,
                conversationId: null,
                action: "InstallationGateRejected",
                payloadJson: payload,
                outcome: AuditOutcomes.Failed),
        };

        // Stage 5.1 iter-4 evaluator feedback item 4 — fail-closed compliance evidence.
        // BOTH the audit row and (when applicable) the outbox dead-letter MUST land before
        // the gate returns to the caller. If either fails, surface an
        // InstallationStateGateComplianceException with BOTH failures attached so the
        // caller's catch block aborts the send AND operators see exactly which compliance
        // sink (audit / outbox) is degraded. Earlier iterations swallowed both failures
        // via try/catch + LogError, which let a rejected send proceed without the durable
        // record — exactly the failure mode flagged by the evaluator.
        //
        // Order: attempt BOTH (best-effort fan-out), collect exceptions, then throw an
        // aggregate. This way audit-store outages do NOT silently leave the outbox row in
        // pending, and outbox-store outages do NOT silently lose the audit row — both
        // attempts run regardless of either failure.
        Exception? auditFailure = null;
        Exception? deadLetterFailure = null;

        try
        {
            await _auditLogger.LogAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            auditFailure = ex;
            _logger.LogError(
                ex,
                "InstallationStateGate: audit logger threw for outbox entry {OutboxEntryId} (question {QuestionId}). " +
                "Compliance evidence is MISSING; failing the gate closed so the caller aborts the send.",
                outboxEntryId,
                question.QuestionId);
        }

        if (!string.IsNullOrEmpty(outboxEntryId))
        {
            try
            {
                await _outbox.DeadLetterAsync(outboxEntryId, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                deadLetterFailure = ex;
                _logger.LogError(
                    ex,
                    "InstallationStateGate: dead-letter failed for outbox entry {OutboxEntryId} (question {QuestionId}). " +
                    "Outbox row will remain in pending state; failing the gate closed so the caller aborts the send.",
                    outboxEntryId,
                    question.QuestionId);
            }
        }

        if (auditFailure is not null || deadLetterFailure is not null)
        {
            var failures = new List<Exception>(2);
            if (auditFailure is not null) failures.Add(auditFailure);
            if (deadLetterFailure is not null) failures.Add(deadLetterFailure);

            var failedSinks = string.Join(
                " and ",
                new[]
                {
                    auditFailure is not null ? "audit logger" : null,
                    deadLetterFailure is not null ? "outbox dead-letter" : null,
                }.Where(s => s is not null));

            throw new InstallationStateGateComplianceException(
                $"InstallationStateGate rejection for question '{question.QuestionId}' could not record compliance evidence: {failedSinks} failed. The gate is failing closed so the caller will not invoke Bot Framework. See inner exceptions for the underlying failure(s).",
                new AggregateException(failures));
        }
    }
}

/// <summary>
/// Raised by <see cref="InstallationStateGate"/> when a rejection decision could not be
/// fully recorded (audit logger and/or outbox dead-letter failed). The gate fails closed:
/// when this exception propagates, the caller MUST treat the send as both rejected AND
/// missing compliance evidence — operators must investigate the underlying audit / outbox
/// store before retrying the send.
/// </summary>
public sealed class InstallationStateGateComplianceException : Exception
{
    /// <summary>Construct a new compliance-failure exception.</summary>
    public InstallationStateGateComplianceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
