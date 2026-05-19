// -----------------------------------------------------------------------
// <copyright file="AuditLogEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;

/// <summary>
/// Stage 5.3 persistence entity for the messenger gateway's audit
/// trail. Backs the <c>audit_logs</c> table exposed by
/// <see cref="AuditDbContext.AuditLogs"/>. Maps from
/// <see cref="AgentSwarm.Messaging.Abstractions.AuditEntry"/> (general
/// path) and
/// <see cref="AgentSwarm.Messaging.Abstractions.HumanResponseAuditEntry"/>
/// (strongly-typed human-response path) into a single table
/// discriminated by <see cref="EntryKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 5.3 brief.</b> The column shape — <c>Id</c>,
/// <c>MessageId</c>, <c>ExternalUserId</c>, <c>AgentId</c>,
/// <c>Action</c>, <c>Timestamp</c>, <c>CorrelationId</c>,
/// <c>TenantId</c>, <c>Details</c> (JSON), <c>Platform</c> — is
/// mandated by the Stage 5.3 brief and architecture.md §3.1
/// "<i>AuditEntry / AuditLogEntry / HumanResponseAuditEntry</i>"
/// (lines 336-355). The persistence schema renames
/// <c>AuditEntry.UserId</c> → <c>ExternalUserId</c> to distinguish
/// the external messenger identifier from any internal identity, and
/// adds two deployment-context columns —
/// <see cref="TenantId"/> (the tenant resolved from the operator's
/// binding) and <see cref="Platform"/> (always <c>"Telegram"</c> for
/// this connector). Human-response-only structured fields
/// (<see cref="QuestionId"/>, <see cref="ActionValue"/>,
/// <see cref="Comment"/>) are nullable here because they are not part
/// of the general-purpose shape; the type-level invariant that they
/// are non-null for human responses is enforced at the
/// <see cref="PersistentAuditLogger"/> mapping boundary and at the
/// abstraction layer by <c>required</c> modifiers on
/// <c>HumanResponseAuditEntry</c>.
/// </para>
/// <para>
/// <b>Immutability.</b> Audit rows are write-only by contract: the
/// <see cref="AgentSwarm.Messaging.Abstractions.IAuditLogger"/>
/// interface exposes only <c>LogAsync</c> /
/// <c>LogHumanResponseAsync</c> — no <c>Update*</c>, <c>Delete*</c>,
/// or <c>Get*</c> method exists, which the Stage 5.3 brief mandates
/// (<i>"Audit records are immutable — no UPDATE/DELETE operations
/// exposed"</i>) and which is asserted at the type level by the
/// <c>IAuditLogger_HasNoMutationMethods</c> reflection test in
/// <c>tests/AgentSwarm.Messaging.Tests</c>. The properties below use
/// <c>{ get; init; }</c> so a row, once constructed, cannot be
/// mutated by client code; replay tooling that needs to inspect a
/// row can do so via <see cref="AuditDbContext"/> read-only queries.
/// </para>
/// </remarks>
public sealed class AuditLogEntry
{
    /// <summary>
    /// Primary key. Mapped from
    /// <c>AuditEntry.EntryId</c> / <c>HumanResponseAuditEntry.EntryId</c>
    /// at the <see cref="PersistentAuditLogger"/> boundary; renamed
    /// from the abstraction's <c>EntryId</c> to <c>Id</c> per the
    /// Stage 5.3 brief column list.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Telegram <c>message_id</c> or callback-query id of the inbound
    /// human reply / command that produced this entry. <c>Null</c>
    /// for general-purpose entries that are not message-scoped (e.g.
    /// scheduled lifecycle events); <c>non-null</c> for entries
    /// mapped from <c>HumanResponseAuditEntry</c> (the abstraction
    /// marks the field <c>required</c>).
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// External messenger user identifier of the actor (e.g. the
    /// Telegram user id). Named <c>ExternalUserId</c> here to
    /// distinguish from any internal identity per architecture.md
    /// §3.1; mapped from the abstraction layer's
    /// <c>AuditEntry.UserId</c> /
    /// <c>HumanResponseAuditEntry.UserId</c>.
    /// </summary>
    public required string ExternalUserId { get; init; }

    /// <summary>
    /// Identifier of the agent involved, when applicable.
    /// <c>Non-null</c> for entries mapped from
    /// <c>HumanResponseAuditEntry</c>.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Canonical action verb. For a slash command this is the bare
    /// command name (<c>ask</c>, <c>status</c>, <c>handoff</c>) so the
    /// Stage 5.3 acceptance assertion "<i>AuditLogEntry exists with
    /// Action=ask</i>" matches verbatim. For a human decision row this
    /// is the <see cref="HumanResponseAuditEntry.ActionValue"/>
    /// (<c>approve</c> / <c>reject</c> / <c>__timeout__</c>) so the
    /// Stage 5.3 acceptance assertion "<i>AuditLogEntry exists with
    /// Action=approve</i>" for an approve button press matches verbatim.
    /// The <see cref="EventFamily"/> column carries the orthogonal
    /// "what kind of event is this" discriminator (command / decision
    /// / handoff / lifecycle) so log queries can filter by family
    /// without overloading this column.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Domain-level discriminator (command / decision / handoff /
    /// lifecycle / general) — see
    /// <see cref="AgentSwarm.Messaging.Abstractions.AuditEventFamilies"/>.
    /// Distinct from <see cref="EntryKind"/> which records which
    /// abstraction record produced the row (write-time provenance);
    /// <c>EventFamily</c> records what the row means at the domain
    /// level (filter dimension for replay / forensic queries).
    /// </summary>
    public required string EventFamily { get; init; }

    /// <summary>UTC timestamp the entry was emitted.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>End-to-end trace identifier for the originating
    /// inbound update / outbound decision.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Tenant the responding operator belongs to (derived from
    /// <c>OperatorBinding.TenantId</c> at mapping time per
    /// architecture.md §3.1). Nullable so the unauthorized-rejection
    /// lifecycle row written by the pipeline before authorization
    /// resolves a binding still has a place to land.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Free-form additional context (JSON). Used by the general
    /// <c>AuditEntry</c> path to carry the raw command arguments /
    /// handoff details / failure reason; <c>null</c> for entries
    /// mapped from <c>HumanResponseAuditEntry</c> (the structured
    /// <see cref="QuestionId"/> / <see cref="ActionValue"/> /
    /// <see cref="Comment"/> columns replace it for that shape).
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Messenger platform identifier. The Stage 5.3 brief mandates
    /// this column is <b>always</b> <c>"Telegram"</c> for this
    /// connector — see <see cref="TelegramPlatform"/>. Persisted as
    /// a column so a future multi-messenger gateway can join audit
    /// rows across connectors without ambiguity.
    /// </summary>
    public required string Platform { get; init; }

    /// <summary>
    /// Discriminator. <c>"general"</c> for rows mapped from
    /// <c>AuditEntry</c>, <c>"human-response"</c> for rows mapped
    /// from <c>HumanResponseAuditEntry</c> (per architecture.md §3.1
    /// EntryKind row). Stored as a string so the value survives a
    /// schema evolution that adds a third shape without renumbering.
    /// See <see cref="AuditEntryKinds"/> for the canonical literals.
    /// </summary>
    public required string EntryKind { get; init; }

    /// <summary>Question id (human-response path only).</summary>
    public string? QuestionId { get; init; }

    /// <summary>Canonical <c>HumanAction.Value</c> (human-response path only).</summary>
    public string? ActionValue { get; init; }

    /// <summary>Optional follow-up comment text (human-response path only).</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Canonical <see cref="Platform"/> literal for this connector
    /// per the Stage 5.3 brief: "<i>Platform (always
    /// 'Telegram')</i>". Centralised here so writers, migrations,
    /// and tests reference one constant rather than duplicating the
    /// string literal.
    /// </summary>
    public const string TelegramPlatform = "Telegram";
}

/// <summary>
/// Canonical string literals for <see cref="AuditLogEntry.EntryKind"/>.
/// </summary>
public static class AuditEntryKinds
{
    /// <summary>Row produced from a general-purpose <c>AuditEntry</c>.</summary>
    public const string General = "general";

    /// <summary>Row produced from a typed <c>HumanResponseAuditEntry</c>.</summary>
    public const string HumanResponse = "human-response";
}
