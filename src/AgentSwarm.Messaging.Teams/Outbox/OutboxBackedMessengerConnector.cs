using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// <see cref="IMessengerConnector"/> decorator that routes
/// <see cref="SendMessageAsync"/> and <see cref="SendQuestionAsync"/> through
/// <see cref="IMessageOutbox.EnqueueAsync"/> rather than invoking the inner
/// connector directly. <see cref="ReceiveAsync"/> is delegated to the inner connector
/// unchanged — inbound events do not flow through the outbox.
/// </summary>
/// <remarks>
/// <para>
/// Implements the Stage 6.1 brief verbatim: "Refactor TeamsProactiveNotifier and
/// TeamsMessengerConnector to remove direct ContinueConversationAsync calls; every send
/// method enqueues an OutboxEntry instead". The wrapped <see cref="TeamsMessengerConnector"/>
/// remains the canonical delivery implementation but is only invoked from
/// <see cref="TeamsOutboxDispatcher"/> after the engine dequeues the entry.
/// </para>
/// <para>
/// <b>Why <see cref="SendMessageAsync"/> resolves the conversation reference here.</b>
/// The outbound <see cref="MessengerMessage.ConversationId"/> uniquely identifies the
/// target Bot Framework conversation; the decorator looks up the persisted
/// <see cref="TeamsConversationReference"/> via <see cref="IConversationReferenceRouter"/>
/// and stamps tenant ID + scope on the entry so the dispatcher can route without a
/// second lookup. The actual reference JSON snapshot is also stamped so the outbox row
/// is fully self-describing.
/// </para>
/// <para>
/// <b>Stage 6.2 step 4 — outbound deduplication with in-flight coordination.</b> Before
/// enqueueing an outbox entry the decorator consults the optional
/// <see cref="OutboundMessageDeduplicator"/> singleton via its <see cref="OutboundMessageDeduplicator.Claim"/>
/// API. The first caller (the "owner") runs the reference-lookup + enqueue pipeline and
/// reports the terminal outcome — <see cref="OutboundMessageDeduplicator.Commit"/> on
/// success (so future racers within the window are suppressed) or
/// <see cref="OutboundMessageDeduplicator.Remove"/> on failure (so retries are
/// permitted). Concurrent racers (the "losers") <i>await</i> the owner's outcome via
/// <see cref="OutboundMessageDeduplicator.ClaimResult.WinnerOutcomeTask"/>: on
/// <c>true</c> they suppress their own enqueue (real duplicate); on <c>false</c> (the
/// owner rolled back due to a transient failure) they re-claim and run the pipeline
/// themselves as the new owner. This guarantees that exactly one outbox row lands per
/// <c>(CorrelationId, DestinationId)</c> tuple within the window <i>even when</i>
/// concurrent sends race and the first attempt fails after the loser has already
/// observed the claim. Iter-3 evaluator fix #1 closes the prior gap where a loser
/// could return success-shaped while the winner rolled back, dropping the send.
/// </para>
/// <para>
/// <b>Failure-mode taxonomy.</b> Two distinct error shapes can surface from
/// <see cref="SendMessageAsync"/>:
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="InvalidOperationException"/> — a <i>permanent</i> routing failure such as
/// "no TeamsConversationReference is registered for ConversationId 'X'" raised by
/// <see cref="EnqueueCoreAsync"/>. Upstream retry policies should NOT loop on this —
/// the user / channel needs to (re-)install the Teams app first.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="OutboundDeduplicationException"/> — a <i>retryable</i> dedupe-coordination
/// failure raised only when the <see cref="MaxClaimAttempts"/> budget is exhausted
/// because every prior in-flight owner rolled back (pathological churn). Carries
/// structured <see cref="OutboundDeduplicationException.CorrelationId"/>,
/// <see cref="OutboundDeduplicationException.ConversationId"/>, and
/// <see cref="OutboundDeduplicationException.Attempts"/> so upstream retry policies can
/// filter on the exception type (no string parsing required) and surface a per-key
/// retry signal. Iter-4 evaluator fix: previously both failure modes shared the
/// <see cref="InvalidOperationException"/> type, forcing message-string inspection to
/// distinguish them.
/// </description>
/// </item>
/// </list>
/// </para>
/// <para>
/// The deduplicator is optional so legacy hosts and tests that pre-date Stage 6.2
/// continue to function without wiring it explicitly.
/// </para>
/// </remarks>
public sealed class OutboxBackedMessengerConnector : IMessengerConnector
{
    /// <summary>
    /// Upper bound on the number of <see cref="OutboundMessageDeduplicator.Claim"/>
    /// attempts a single <see cref="SendMessageAsync"/> call will make before
    /// surfacing failure to the caller as an
    /// <see cref="OutboundDeduplicationException"/>. Two attempts is the canonical
    /// pattern (mirrors the card-action handler's <c>maxClaimRetries = 2</c>): one as
    /// the loser awaiting the original owner, one as the new owner after the original
    /// rolled back. A third pathological round (e.g. every new owner crashes
    /// immediately between Claim and Commit) is unlikely and is surfaced as a
    /// retryable typed exception rather than spinning forever.
    /// </summary>
    private const int MaxClaimAttempts = 2;

    private readonly IMessengerConnector _innerConnector;
    private readonly IMessageOutbox _outbox;
    private readonly IConversationReferenceRouter _conversationReferenceRouter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxBackedMessengerConnector> _logger;
    private readonly OutboundMessageDeduplicator? _outboundDeduplicator;

    /// <summary>Construct the decorator (legacy 5-arg overload — no outbound deduplicator wired).</summary>
    /// <param name="innerConnector">The wrapped <see cref="TeamsMessengerConnector"/>. Used for <see cref="ReceiveAsync"/> only.</param>
    /// <param name="outbox">Outbox queue for outbound deliveries.</param>
    /// <param name="conversationReferenceRouter">Router used to resolve the tenant scope for outbound messages.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Optional clock (defaults to <see cref="TimeProvider.System"/>).</param>
    public OutboxBackedMessengerConnector(
        IMessengerConnector innerConnector,
        IMessageOutbox outbox,
        IConversationReferenceRouter conversationReferenceRouter,
        ILogger<OutboxBackedMessengerConnector> logger,
        TimeProvider? timeProvider = null)
        : this(innerConnector, outbox, conversationReferenceRouter, logger, timeProvider, outboundDeduplicator: null)
    {
    }

    /// <summary>
    /// Stage 6.2 canonical constructor — accepts an optional
    /// <see cref="OutboundMessageDeduplicator"/> so duplicate outbound
    /// <see cref="MessengerMessage"/> enqueues (same
    /// <see cref="MessengerMessage.CorrelationId"/> + same
    /// <see cref="MessengerMessage.ConversationId"/>) within the configured window are
    /// suppressed at the decorator boundary.
    /// </summary>
    public OutboxBackedMessengerConnector(
        IMessengerConnector innerConnector,
        IMessageOutbox outbox,
        IConversationReferenceRouter conversationReferenceRouter,
        ILogger<OutboxBackedMessengerConnector> logger,
        TimeProvider? timeProvider,
        OutboundMessageDeduplicator? outboundDeduplicator)
    {
        _innerConnector = innerConnector ?? throw new ArgumentNullException(nameof(innerConnector));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _conversationReferenceRouter = conversationReferenceRouter ?? throw new ArgumentNullException(nameof(conversationReferenceRouter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _outboundDeduplicator = outboundDeduplicator;
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Fast path — no deduplicator wired (legacy 5-arg constructor) or the message
        // lacks the keys needed for dedupe. Run the pipeline directly without any
        // coordination.
        if (_outboundDeduplicator is null
            || string.IsNullOrEmpty(message.CorrelationId)
            || string.IsNullOrEmpty(message.ConversationId))
        {
            await EnqueueCoreAsync(message, ct).ConfigureAwait(false);
            return;
        }

        // Stage 6.2 step 4 — suppress duplicate (CorrelationId, ConversationId) pairs
        // within the configured window with in-flight coordination so concurrent
        // losers do NOT return success-shaped while the winner is still mid-enqueue.
        //
        // Iter-3 evaluator fix #1: previously the loser short-circuited as soon as
        // TryRegister observed an existing entry, even if the winning thread had not
        // yet reached EnqueueAsync. If the winner then threw (transient infrastructure
        // failure) and rolled back the slot via Remove, the loser's caller had already
        // received a successful-no-op response and would never retry — silently
        // dropping the send. The Claim API now exposes the winner's outcome task so
        // the loser blocks until the winner commits (suppress as real duplicate) or
        // rolls back (re-claim and retry as the new owner). A bounded retry loop
        // prevents pathological churn if every claimed owner crashes immediately.
        for (var attempt = 1; ; attempt++)
        {
            var claim = _outboundDeduplicator.Claim(message.CorrelationId, message.ConversationId);
            if (claim.IsOwner)
            {
                try
                {
                    await EnqueueCoreAsync(message, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Release the slot so the caller's retry — or a sibling pod's
                    // parallel attempt for the same correlation — can actually
                    // re-enqueue. Without this, the next 10 minutes (or whatever
                    // window the host configured) would silently swallow every retry
                    // of a failed send. Remove also signals concurrent loser waiters
                    // with `false` so they re-claim and run the pipeline themselves.
                    _outboundDeduplicator.Remove(message.CorrelationId, message.ConversationId);
                    throw;
                }

                // Successful enqueue — signal concurrent loser waiters with `true` so
                // they suppress as real duplicates, and leave the entry in the
                // dictionary so future claimants within the window are short-circuited.
                _outboundDeduplicator.Commit(message.CorrelationId, message.ConversationId);
                return;
            }

            // Loser path — block until the in-flight owner reports its outcome.
            bool winnerCommitted;
            try
            {
                winnerCommitted = await claim.WinnerOutcomeTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (winnerCommitted)
            {
                _logger.LogInformation(
                    "Suppressing duplicate outbound MessengerMessage {MessageId} (correlation {CorrelationId}) -> conversation {ConversationId}; another send for the same CorrelationId + DestinationId committed an outbox entry within the dedupe window.",
                    message.MessageId,
                    message.CorrelationId,
                    message.ConversationId);
                return;
            }

            // Winner rolled back. Re-claim as the new owner and retry the pipeline
            // ourselves — bounded so a pathological pattern (every claim's first owner
            // crashes immediately) cannot spin forever.
            //
            // Iter-4 evaluator fix: the exhaustion failure is surfaced as a dedicated
            // OutboundDeduplicationException carrying CorrelationId / ConversationId /
            // Attempts so upstream retry policies can filter on the exception TYPE
            // (and inspect structured properties) instead of string-matching against
            // the InvalidOperationException raised by EnqueueCoreAsync's
            // missing-reference path — which is a permanent failure that must NOT be
            // looped on.
            if (attempt >= MaxClaimAttempts)
            {
                _logger.LogWarning(
                    "Outbound deduplicator retry budget exhausted for MessengerMessage {MessageId} (correlation {CorrelationId}) -> conversation {ConversationId} after {Attempts} attempts; surfacing failure so the caller can retry.",
                    message.MessageId,
                    message.CorrelationId,
                    message.ConversationId,
                    attempt);
                throw new OutboundDeduplicationException(
                    $"Outbound deduplicator could not claim ('{message.CorrelationId}', '{message.ConversationId}') after {attempt} attempts because every prior in-flight owner rolled back; the caller should retry.",
                    correlationId: message.CorrelationId,
                    conversationId: message.ConversationId,
                    attempts: attempt,
                    messageId: message.MessageId);
            }
        }
    }

    /// <summary>
    /// Run the reference-lookup + outbox-enqueue pipeline for the supplied
    /// <paramref name="message"/>. Extracted so both the dedupe-fast-path and the
    /// dedupe-coordinated paths in <see cref="SendMessageAsync"/> share identical
    /// enqueue semantics. Throws on missing reference or outbox failure — both error
    /// modes are caught by <see cref="SendMessageAsync"/> when running under the
    /// deduplicator so the slot can be rolled back before the exception propagates.
    /// </summary>
    private async Task EnqueueCoreAsync(MessengerMessage message, CancellationToken ct)
    {
        // Resolve the persisted reference via the router so we can capture the snapshot
        // and the tenant ID. The lookup is intentionally identical to what
        // TeamsMessengerConnector.SendMessageAsync does at dispatch time — failing here
        // surfaces missing-reference errors at enqueue rather than after the delivery
        // window opens.
        var stored = await _conversationReferenceRouter
            .GetByConversationIdAsync(message.ConversationId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No TeamsConversationReference is registered for ConversationId '{message.ConversationId}'; refusing to enqueue an outbound MessengerMessage that cannot be routed.");

        var entry = new OutboxEntry
        {
            OutboxEntryId = Guid.NewGuid().ToString("N"),
            CorrelationId = message.CorrelationId,
            Destination = $"teams://{Uri.EscapeDataString(stored.TenantId)}/conversation/{Uri.EscapeDataString(message.ConversationId)}",
            DestinationType = null,
            DestinationId = message.ConversationId,
            PayloadType = OutboxPayloadTypes.MessengerMessage,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new TeamsOutboxPayloadEnvelope { Message = message },
                TeamsOutboxPayloadEnvelope.JsonOptions),
            ConversationReferenceJson = stored.ReferenceJson,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for outbound MessengerMessage {MessageId} (correlation {CorrelationId}) -> conversation {ConversationId}.",
            entry.OutboxEntryId,
            message.MessageId,
            message.CorrelationId,
            message.ConversationId);
    }

    /// <inheritdoc />
    public async Task SendQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);

        var validationErrors = question.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' is invalid: {string.Join("; ", validationErrors)}");
        }

        // Resolve via the question's own routing fields — exactly one of TargetUserId /
        // TargetChannelId is set (enforced by Validate()).
        TeamsConversationReference stored;
        string destinationType;
        string destinationId;
        string destination;
        if (!string.IsNullOrWhiteSpace(question.TargetUserId))
        {
            stored = await _conversationReferenceRouter
                .GetByConversationIdAsync(question.TargetUserId!, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"AgentQuestion '{question.QuestionId}' targets user '{question.TargetUserId}' but no TeamsConversationReference is registered for that ID.");
            destinationType = OutboxDestinationTypes.Personal;
            destinationId = question.TargetUserId!;
            destination = $"teams://{Uri.EscapeDataString(question.TenantId)}/user/{Uri.EscapeDataString(destinationId)}";
        }
        else
        {
            stored = await _conversationReferenceRouter
                .GetByConversationIdAsync(question.TargetChannelId!, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"AgentQuestion '{question.QuestionId}' targets channel '{question.TargetChannelId}' but no TeamsConversationReference is registered for that ID.");
            destinationType = OutboxDestinationTypes.Channel;
            destinationId = question.TargetChannelId!;
            destination = $"teams://{Uri.EscapeDataString(question.TenantId)}/channel/{Uri.EscapeDataString(destinationId)}";
        }

        var entry = new OutboxEntry
        {
            OutboxEntryId = Guid.NewGuid().ToString("N"),
            CorrelationId = question.CorrelationId,
            Destination = destination,
            DestinationType = destinationType,
            DestinationId = destinationId,
            PayloadType = OutboxPayloadTypes.AgentQuestion,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new TeamsOutboxPayloadEnvelope { Question = question },
                TeamsOutboxPayloadEnvelope.JsonOptions),
            ConversationReferenceJson = stored.ReferenceJson,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for AgentQuestion {QuestionId} (correlation {CorrelationId}) -> {DestinationType} {DestinationId}.",
            entry.OutboxEntryId,
            question.QuestionId,
            question.CorrelationId,
            destinationType,
            destinationId);
    }

    /// <inheritdoc />
    public Task<MessengerEvent> ReceiveAsync(CancellationToken ct)
        => _innerConnector.ReceiveAsync(ct);
}

/// <summary>
/// Thrown by <see cref="OutboxBackedMessengerConnector.SendMessageAsync"/> when the
/// <see cref="OutboundMessageDeduplicator"/> claim loop exhausts its bounded retry
/// budget because every prior in-flight owner rolled back (pathological churn —
/// e.g. every claimed owner crashes between <see cref="OutboundMessageDeduplicator.Claim"/>
/// and <see cref="OutboundMessageDeduplicator.Commit"/>).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the <see cref="InvalidOperationException"/> raised by the decorator's
/// reference-lookup path ("No TeamsConversationReference is registered for
/// ConversationId 'X'…") because the two failure modes have <i>opposite</i> retry
/// semantics:
/// <list type="bullet">
/// <item>
/// <description>
/// The missing-reference <see cref="InvalidOperationException"/> is <b>permanent</b>
/// from the dispatcher's point of view — the user / channel must (re-)install the
/// Teams app before any retry can succeed, so upstream policies should dead-letter
/// or alert rather than loop.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="OutboundDeduplicationException"/> is <b>retryable</b> — the underlying
/// send pipeline never executed, so the caller (or sibling pod) can safely
/// re-enqueue the same <see cref="MessengerMessage"/> and expect success once the
/// dedupe churn settles.
/// </description>
/// </item>
/// </list>
/// Iter-4 evaluator fix: previously both modes shared the
/// <see cref="InvalidOperationException"/> type, forcing upstream retry policies to
/// inspect the exception message string to decide whether to loop. A dedicated type
/// (with structured properties) lets policies filter via a single <c>catch</c> clause.
/// </para>
/// <para>
/// The structured properties (<see cref="CorrelationId"/>, <see cref="ConversationId"/>,
/// <see cref="Attempts"/>, <see cref="MessageId"/>) are populated so audit logs and
/// metrics can attribute the failure without re-parsing <see cref="Exception.Message"/>.
/// </para>
/// </remarks>
public sealed class OutboundDeduplicationException : Exception
{
    /// <summary>
    /// <see cref="MessengerMessage.CorrelationId"/> of the send that exhausted the
    /// claim retry budget. Half of the dedupe key.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// <see cref="MessengerMessage.ConversationId"/> of the send that exhausted the
    /// claim retry budget. The other half of the dedupe key.
    /// </summary>
    public string ConversationId { get; }

    /// <summary>
    /// Number of <see cref="OutboundMessageDeduplicator.Claim"/> attempts the decorator
    /// made before surfacing the failure. Equals
    /// <c>OutboxBackedMessengerConnector.MaxClaimAttempts</c> at throw time.
    /// </summary>
    public int Attempts { get; }

    /// <summary>
    /// <see cref="MessengerMessage.MessageId"/> of the originating message — preserved
    /// so structured logs and audit entries can correlate the throw with the inbound
    /// send call without re-parsing <see cref="Exception.Message"/>. May be null when
    /// the originating message did not carry a stable identifier.
    /// </summary>
    public string? MessageId { get; }

    /// <summary>
    /// Construct the exception. The decorator is the only intended caller.
    /// </summary>
    /// <param name="message">Human-readable failure description (logged + surfaced via <see cref="Exception.Message"/>).</param>
    /// <param name="correlationId">Half of the dedupe key — see <see cref="CorrelationId"/>.</param>
    /// <param name="conversationId">Other half of the dedupe key — see <see cref="ConversationId"/>.</param>
    /// <param name="attempts">Number of claim attempts made — see <see cref="Attempts"/>.</param>
    /// <param name="messageId">Originating message identifier — see <see cref="MessageId"/>.</param>
    public OutboundDeduplicationException(
        string message,
        string correlationId,
        string conversationId,
        int attempts,
        string? messageId = null)
        : base(message)
    {
        CorrelationId = correlationId ?? throw new ArgumentNullException(nameof(correlationId));
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
        Attempts = attempts;
        MessageId = messageId;
    }
}
