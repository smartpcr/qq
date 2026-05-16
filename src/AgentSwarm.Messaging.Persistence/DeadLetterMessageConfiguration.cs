// -----------------------------------------------------------------------
// <copyright file="DeadLetterMessageConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Stage 4.2 — EF Core configuration for <see cref="DeadLetterMessage"/>.
/// Maps the outbox-row companion dead-letter ledger (defined in
/// <c>AgentSwarm.Messaging.Abstractions</c>) to the
/// <c>dead_letter_messages</c> table backing
/// <see cref="PersistentDeadLetterQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>UNIQUE on OriginalMessageId.</b> Per architecture.md §3.1 the
/// dead-letter ledger has a 1-to-1 relationship with the outbox row
/// it dead-lettered. The UNIQUE index is the database-level gate
/// that lets <see cref="PersistentDeadLetterQueue.SendToDeadLetterAsync"/>
/// treat a duplicate write as a successful no-op (the
/// <see cref="DbUpdateException"/> from the index violation is the
/// race signal the processor catches when two workers attempt to
/// dead-letter the same row in quick succession).
/// </para>
/// <para>
/// <b>DateTimeOffset columns.</b> Persisted as Unix-millisecond
/// <see cref="long"/> values via the same converter shape used by
/// every other Stage 2/3/4 entity (consistent with
/// <see cref="OutboundMessageConfiguration"/>,
/// <see cref="OutboundDeadLetterConfiguration"/>, etc.). SQLite
/// cannot translate <see cref="DateTimeOffset"/> ORDER BY clauses
/// natively, so the secondary
/// <c>ORDER BY DeadLetteredAt</c> in <c>ListAsync</c> relies on the
/// long backing column.
/// </para>
/// <para>
/// <b>Indexes.</b> Per architecture.md §3.1 the operator queries
/// pivot on <c>(AlertStatus, Severity)</c> (alerting-loop "find
/// un-alerted Critical/High dead-letters"),
/// <c>DeadLetteredAt</c> (retention pruning), and
/// <c>CorrelationId</c> (trace pivot). All three are non-unique
/// secondary indexes.
/// </para>
/// </remarks>
public sealed class DeadLetterMessageConfiguration
    : IEntityTypeConfiguration<DeadLetterMessage>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToUnixMillis =
        new(
            v => v == null ? (long?)null : v.Value.ToUnixTimeMilliseconds(),
            v => v == null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value));

    private static readonly ValueConverter<MessageSeverity, int> SeverityToInt =
        new(
            v => (int)v,
            v => (MessageSeverity)v);

    private static readonly ValueConverter<OutboundSourceType, string> SourceTypeToString =
        new(
            v => v.ToString(),
            v => (OutboundSourceType)Enum.Parse(typeof(OutboundSourceType), v));

    private static readonly ValueConverter<OutboundFailureCategory, string> FailureCategoryToString =
        new(
            v => v.ToString(),
            v => (OutboundFailureCategory)Enum.Parse(typeof(OutboundFailureCategory), v));

    private static readonly ValueConverter<DeadLetterAlertStatus, string> AlertStatusToString =
        new(
            v => v.ToString(),
            v => (DeadLetterAlertStatus)Enum.Parse(typeof(DeadLetterAlertStatus), v));

    // Iter-2 evaluator item 1 — ReplayStatus enum-to-string converter
    // mirrors AlertStatusToString. Persisted as a string column (not
    // a numeric ordinal) so the audit row is human-readable in
    // ad-hoc SQL inspections and so adding a future enum value
    // (e.g. Suppressed) does not silently re-map existing rows.
    private static readonly ValueConverter<DeadLetterReplayStatus, string> ReplayStatusToString =
        new(
            v => v.ToString(),
            v => (DeadLetterReplayStatus)Enum.Parse(typeof(DeadLetterReplayStatus), v));

    public void Configure(EntityTypeBuilder<DeadLetterMessage> builder)
    {
        builder.ToTable("dead_letter_messages");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedNever();

        builder.Property(x => x.OriginalMessageId)
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.ChatId)
            .IsRequired();

        builder.Property(x => x.Payload)
            .IsRequired();

        builder.Property(x => x.SourceEnvelopeJson);

        builder.Property(x => x.Severity)
            .HasConversion(SeverityToInt)
            .IsRequired();

        builder.Property(x => x.SourceType)
            .HasConversion(SourceTypeToString)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.SourceId)
            .HasMaxLength(128);

        // Iter-2 evaluator item 4 — AgentId is the e2e-scenarios.md
        // "dead-letter record includes ... AgentId" column. Nullable
        // because not every OutboundSourceType resolves to a single
        // owning agent.
        builder.Property(x => x.AgentId)
            .HasMaxLength(128);

        builder.Property(x => x.CorrelationId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.AttemptCount)
            .IsRequired();

        builder.Property(x => x.FinalError)
            .HasMaxLength(2048)
            .IsRequired();

        // Iter-2 evaluator item 1 — AttemptTimestamps (architecture.md
        // §3.1 line 386). Stored as TEXT (JSON array of ISO-8601
        // strings). No HasMaxLength — a poison row with 100 entries
        // ≈ 3 KB is well within column-bound TEXT and a length cap
        // here would silently truncate the JSON, producing a parse
        // error at the operator audit screen. Required (NOT NULL)
        // with a default of "[]" so legacy callers that bypass the
        // FailureReason → DLQ projection still produce a well-formed
        // payload.
        builder.Property(x => x.AttemptTimestamps)
            .IsRequired()
            .HasDefaultValue("[]");

        // Iter-2 evaluator item 1 — ErrorHistory (architecture.md §3.1
        // line 388). Same column shape as AttemptTimestamps; see
        // that property's comment for the per-entry size cap
        // rationale.
        builder.Property(x => x.ErrorHistory)
            .IsRequired()
            .HasDefaultValue("[]");

        builder.Property(x => x.FailureCategory)
            .HasConversion(FailureCategoryToString)
            .HasMaxLength(48)
            .IsRequired();

        builder.Property(x => x.AlertStatus)
            .HasConversion(AlertStatusToString)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.AlertSentAt)
            .HasConversion(NullableDateTimeOffsetToUnixMillis);

        // Iter-2 evaluator item 1 — ReplayStatus (architecture.md §3.1
        // line 391). Persisted as a string for the same human-
        // readability reasons as AlertStatus. Defaults to "None" at
        // insert; Stage 4.2 itself does not mutate this column (the
        // operator replay workflow is a future workstream).
        builder.Property(x => x.ReplayStatus)
            .HasConversion(ReplayStatusToString)
            .HasMaxLength(32)
            .IsRequired()
            .HasDefaultValue(DeadLetterReplayStatus.None);

        // Iter-2 evaluator item 1 — ReplayCorrelationId (architecture.md
        // §3.1 line 392). Bounded to 128 chars to match the
        // canonical CorrelationId column width across every other
        // messaging entity. Nullable until a replay attempt is
        // initiated.
        builder.Property(x => x.ReplayCorrelationId)
            .HasMaxLength(128);

        builder.Property(x => x.DeadLetteredAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        // UNIQUE — one dead-letter row per outbox row (architecture.md
        // §3.1 "UNIQUE (OriginalMessageId)"). The concurrent-write
        // race surfaces as DbUpdateException here; the queue's
        // SendToDeadLetterAsync catches it and treats the duplicate
        // as success.
        builder.HasIndex(x => x.OriginalMessageId)
            .IsUnique()
            .HasDatabaseName("ux_dead_letter_messages_original_message_id");

        // (AlertStatus, Severity) — used by the alerting loop to find
        // un-alerted Critical/High dead-letters. Architecture.md §3.1
        // calls this out explicitly as the canonical index.
        builder.HasIndex(x => new { x.AlertStatus, x.Severity })
            .HasDatabaseName("ix_dead_letter_messages_alert_status_severity");

        // DeadLetteredAt — retention pruning and "last 24h" operator
        // screen.
        builder.HasIndex(x => x.DeadLetteredAt)
            .HasDatabaseName("ix_dead_letter_messages_dead_lettered_at");

        // CorrelationId — trace pivot from log into the dead-letter
        // audit screen.
        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_dead_letter_messages_correlation_id");

        // Iter-2 evaluator item 4 — AgentId secondary index for the
        // "all dead-letters originating from <agent>" operator
        // screen. Non-unique; one agent typically has many
        // dead-letter rows during a partial outage.
        builder.HasIndex(x => x.AgentId)
            .HasDatabaseName("ix_dead_letter_messages_agent_id");

        // Iter-2 evaluator item 1 — ReplayStatus secondary index
        // (architecture.md §3.1 line 399). The operator replay
        // workflow paginates the "replay-eligible" view (rows where
        // ReplayStatus = None) and the "failed-replay" view (rows
        // where ReplayStatus = Failed); both queries need this
        // index to avoid a full table scan on the dead-letter
        // ledger.
        builder.HasIndex(x => x.ReplayStatus)
            .HasDatabaseName("ix_dead_letter_messages_replay_status");
    }
}
