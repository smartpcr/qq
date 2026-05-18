// -----------------------------------------------------------------------
// <copyright file="IAuditFallbackSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Durable secondary sink that guarantees an inbound-command
/// rejection audit row survives a transient <see cref="IAuditLogger"/>
/// failure. The Stage 5.3 brief mandates "<i>log every inbound
/// command</i>"; the pipeline-level rejection sites (parse-empty,
/// parse-invalid, authorize-denied, role-denied) cannot rethrow on
/// audit failure (the denial response is security-critical and must
/// reach the operator even when the audit DB is down), so the
/// rejection's audit row would otherwise be silently dropped. This
/// sink is the durable backstop: on every audit-DB failure the
/// pipeline enqueues the entry here so the row is persisted to a
/// separate medium. The production binding registered by
/// <c>ServiceCollectionExtensions.AddMessagingPersistence</c> is
/// <c>FileAuditFallbackSink</c>, which appends each entry to a local
/// JSONL file alongside the audit DB. Operator-side replay of those
/// lines back into <c>audit_logs</c> is intentionally not part of
/// this workstream; Stage 5.3's contract is durability of the row,
/// and ops teams use any standard JSONL-replay tooling once the
/// audit DB returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> Implementations MUST be safe to call from any
/// thread (the pipeline calls the sink inside its fixed-ordering
/// stages). Implementations MUST persist the entry to a durable
/// medium BEFORE returning — an in-memory implementation defeats the
/// "no silent drop on audit-DB outage" guarantee. The default
/// <see cref="NullAuditFallbackSink"/> is the explicit opt-OUT;
/// production hosts call
/// <c>ServiceCollectionExtensions.AddMessagingPersistence</c> (in
/// <c>AgentSwarm.Messaging.Persistence</c>) which replaces this
/// binding with <c>FileAuditFallbackSink</c>, the real file-backed
/// implementation.
/// </para>
/// <para>
/// <b>Why a separate interface (not just a second IAuditLogger).</b>
/// The fallback path is invoked ONLY when the primary
/// <see cref="IAuditLogger"/> throws. Modeling it as a second
/// <see cref="IAuditLogger"/> would let a caller accidentally use
/// it as the primary (and skip the DB write entirely). A distinct
/// interface keeps the call-site discipline: <c>_audit.LogAsync</c>
/// first, <c>_fallback.EnqueueAsync</c> only inside the catch.
/// </para>
/// <para>
/// <b>Idempotency.</b> Any operator-side replay tooling that
/// re-inserts a fallback line into <c>audit_logs</c> may attempt the
/// insert more than once (e.g. crash between successful DB write
/// and the operator marking the line as processed). The audit
/// table's primary key on <see cref="AuditEntry.EntryId"/> /
/// <see cref="HumanResponseAuditEntry.EntryId"/> absorbs the
/// duplicate insert as a unique-key violation, mirroring the same
/// duplicate-publish primitive that decision-event handlers tolerate
/// (architecture.md §10.3 consumer-side dedup on QuestionId).
/// </para>
/// </remarks>
public interface IAuditFallbackSink
{
    /// <summary>
    /// Enqueue a general-purpose <see cref="AuditEntry"/> for durable
    /// fallback storage. MUST persist before returning.
    /// </summary>
    Task EnqueueAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>
    /// Enqueue a typed <see cref="HumanResponseAuditEntry"/> for
    /// durable fallback storage. MUST persist before returning.
    /// </summary>
    Task EnqueueAsync(HumanResponseAuditEntry entry, CancellationToken ct);
}
