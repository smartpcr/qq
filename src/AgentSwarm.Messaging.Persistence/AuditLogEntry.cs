// -----------------------------------------------------------------------
// <copyright file="AuditLogEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;

/// <summary>
/// Persistence entity for the messenger gateway's audit trail. Maps
/// from <c>AgentSwarm.Messaging.Abstractions.AuditEntry</c> (general
/// path) and <c>AgentSwarm.Messaging.Abstractions.HumanResponseAuditEntry</c>
/// (strongly-typed human-response path) into a single
/// <c>audit_log_entries</c> table discriminated by <see cref="EntryKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-table-per-shape via discriminator.</b> Both abstraction
/// records share the same backing table because their column sets
/// substantially overlap; the <see cref="EntryKind"/> column records
/// which abstraction shape produced the row so a future query can
/// reconstruct the typed record. Human-response-only fields
/// (<see cref="QuestionId"/>, <see cref="ActionValue"/>,
/// <see cref="Comment"/>) are nullable here because they are not part
/// of the general-purpose shape; the type-level invariant that they
/// are non-null for human responses is enforced at the
/// <see cref="PersistentAuditLogger"/> mapping boundary (and at the
/// abstraction layer by <c>required</c> modifiers on
/// <c>HumanResponseAuditEntry</c>).
/// </para>
/// <para>
/// <b>Stage 3.2 scope.</b> This is the minimal viable shape that
/// satisfies the story brief's audit requirement
/// (<i>"Persist every human response with message ID, user ID, agent
/// ID, timestamp, and correlation ID"</i>) plus the Stage 3.2 brief's
/// <c>HandoffCommandHandler</c> audit obligation. Stage 5.3 extends
/// this schema with tenant / platform / details discrimination per
/// <c>architecture.md</c> §3.1 — those columns can be added via a
/// follow-up migration without rewriting the writer.
/// </para>
/// </remarks>
public sealed class AuditLogEntry
{
    public required Guid EntryId { get; init; }

    /// <summary>Indicates which abstraction record produced this row.</summary>
    public required AuditEntryKind EntryKind { get; init; }

    public string? MessageId { get; init; }

    public required string UserId { get; init; }

    public string? AgentId { get; init; }

    public required string Action { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Free-form details JSON (general path only).</summary>
    public string? Details { get; init; }

    /// <summary>Question id (human-response path only).</summary>
    public string? QuestionId { get; init; }

    /// <summary>Canonical <c>HumanAction.Value</c> (human-response path only).</summary>
    public string? ActionValue { get; init; }

    /// <summary>Optional follow-up comment text (human-response path only).</summary>
    public string? Comment { get; init; }
}

/// <summary>Discriminator value for <see cref="AuditLogEntry.EntryKind"/>.</summary>
public enum AuditEntryKind
{
    /// <summary>Row produced from a general-purpose <c>AuditEntry</c>.</summary>
    General = 0,

    /// <summary>Row produced from a typed <c>HumanResponseAuditEntry</c>.</summary>
    HumanResponse = 1,
}
