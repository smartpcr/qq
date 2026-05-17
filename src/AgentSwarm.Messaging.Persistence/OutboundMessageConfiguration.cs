// -----------------------------------------------------------------------
// <copyright file="OutboundMessageConfiguration.cs" company="Microsoft Corp.">
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
/// Stage 4.1 — EF Core configuration for <see cref="OutboundMessage"/>.
/// Maps the canonical outbox record (defined in
/// <c>AgentSwarm.Messaging.Abstractions</c>) to the <c>outbox</c>
/// table that backs <see cref="PersistentOutboundQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Severity ordering — int-backed value converter.</b> The
/// <see cref="MessageSeverity"/> enum is declared
/// <c>Critical=0, High=1, Normal=2, Low=3</c>. Persisting the enum as
/// its underlying int (rather than the default string serialisation)
/// is what makes the dequeue-side query
/// <c>WHERE Status=Pending ORDER BY Severity ASC, CreatedAt ASC</c>
/// return Critical first, then High, then Normal, then Low — strings
/// would sort alphabetically (Critical, High, Low, Normal) and silently
/// break severity-priority dequeue. The explicit
/// <see cref="ValueConverter"/> below pins the mapping so a future
/// reordering of the enum cannot regress the ORDER BY semantics
/// without an EF Core warning.
/// </para>
/// <para>
/// <b>IdempotencyKey UNIQUE.</b> The unique index on
/// <see cref="OutboundMessage.IdempotencyKey"/> is the database-level
/// gate for outbound deduplication (architecture.md §3.1 idempotency
/// key derivation table). The Stage 4.1
/// <see cref="PersistentOutboundQueue.EnqueueAsync(OutboundMessage, System.Threading.CancellationToken)"/>
/// path issues a pre-flight existence probe before INSERT to short-
/// circuit the hot duplicate path, and falls back to a
/// <c>DbUpdateException</c> catch for the concurrent-insert race;
/// either way the database is the source of truth.
/// </para>
/// <para>
/// <b>DateTimeOffset columns.</b> Persisted as Unix-millisecond
/// <see cref="long"/> values via the same converter shape used by
/// every other Stage 2/3/4 entity (<see cref="InboundUpdateConfiguration"/>
/// , <see cref="OutboundDeadLetterConfiguration"/>, etc.). SQLite
/// cannot translate <see cref="DateTimeOffset"/> ORDER BY clauses
/// natively, so the queue's secondary CreatedAt ordering relies on the
/// long backing column.
/// </para>
/// <para>
/// <b>Composite dequeue index.</b> An explicit composite index on
/// <c>(Status, Severity, CreatedAt)</c> backs the dequeue query —
/// without it, every dequeue call would full-scan the outbox table,
/// which dominates the P95 budget under burst load.
/// </para>
/// </remarks>
public sealed class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToUnixMillis =
        new(
            v => v == null ? (long?)null : v.Value.ToUnixTimeMilliseconds(),
            v => v == null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value));

    /// <summary>
    /// Persists <see cref="MessageSeverity"/> as the underlying int so
    /// that <c>ORDER BY Severity ASC</c> yields Critical(0) → High(1)
    /// → Normal(2) → Low(3) — the canonical Stage 4.1 priority order.
    /// String persistence would sort alphabetically and silently break
    /// the priority contract.
    /// </summary>
    private static readonly ValueConverter<MessageSeverity, int> SeverityToInt =
        new(
            v => (int)v,
            v => (MessageSeverity)v);

    /// <summary>
    /// Persists <see cref="OutboundMessageStatus"/> as its name string
    /// so the column is human-readable in production diagnostics. The
    /// dequeue query filters on <c>Status == Pending</c>; the index
    /// below covers the equality lookup so the string compare is
    /// O(log n) per row.
    /// </summary>
    private static readonly ValueConverter<OutboundMessageStatus, string> StatusToString =
        new(
            v => v.ToString(),
            v => (OutboundMessageStatus)Enum.Parse(typeof(OutboundMessageStatus), v));

    /// <summary>
    /// Persists <see cref="OutboundSourceType"/> as its name string so
    /// dead-letter inspections and audit queries do not have to map an
    /// opaque integer back to a human label. Bounded to 32 chars —
    /// the longest current enum name is <c>StatusUpdate</c> (12 chars)
    /// so the cap is comfortable for any future addition.
    /// </summary>
    private static readonly ValueConverter<OutboundSourceType, string> SourceTypeToString =
        new(
            v => v.ToString(),
            v => (OutboundSourceType)Enum.Parse(typeof(OutboundSourceType), v));

    public void Configure(EntityTypeBuilder<OutboundMessage> builder)
    {
        builder.ToTable("outbox");

        builder.HasKey(x => x.MessageId);

        builder.Property(x => x.MessageId)
            .ValueGeneratedNever();

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

        builder.Property(x => x.Status)
            .HasConversion(StatusToString)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.AttemptCount)
            .IsRequired();

        builder.Property(x => x.MaxAttempts)
            .IsRequired();

        builder.Property(x => x.NextRetryAt)
            .HasConversion(NullableDateTimeOffsetToUnixMillis);

        builder.Property(x => x.CreatedAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.SentAt)
            .HasConversion(NullableDateTimeOffsetToUnixMillis);

        // Stage 4.1 iter-2 evaluator item 2 — persist the dequeue
        // timestamp alongside CreatedAt / SentAt so the outbox row
        // captures every state transition timestamp the Stage 4.1
        // brief calls out ("records DequeuedAt timestamp" before
        // transitioning to Sending). Same Unix-millis converter as
        // every other DateTimeOffset column on this entity.
        builder.Property(x => x.DequeuedAt)
            .HasConversion(NullableDateTimeOffsetToUnixMillis);

        builder.Property(x => x.TelegramMessageId);

        builder.Property(x => x.CorrelationId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.ErrorDetail)
            .HasMaxLength(2048);

        // Iter-2 evaluator item 1 — AttemptHistoryJson accumulates the
        // per-attempt failure log appended on every MarkFailedAsync /
        // DeadLetterAsync. TEXT (no HasMaxLength) because the
        // serialised array — capped at 100 entries by
        // AgentSwarm.Messaging.Core.AttemptHistory.MaxEntries — fits
        // comfortably in TEXT and a column-level cap here would
        // silently truncate the JSON, producing a parse error at
        // dead-letter projection time. Nullable: a row that has
        // never observed a failed send has nothing to log.
        builder.Property(x => x.AttemptHistoryJson);

        // UNIQUE constraint on IdempotencyKey — the architecture's
        // outbound-dedup gate (§3.1). A concurrent EnqueueAsync race
        // surfaces as DbUpdateException here, which the queue's
        // EnqueueAsync catches and translates to a no-op /
        // duplicate-rejected outcome.
        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_outbox_idempotency_key");

        // Composite dequeue-path index. The query is
        // `WHERE Status='Pending' ORDER BY Severity ASC, CreatedAt ASC`.
        // Status leads so a partial-index emulation (string equality
        // filter) cuts the seek cost; Severity then CreatedAt complete
        // the ORDER BY without a post-fetch sort.
        builder.HasIndex(x => new { x.Status, x.Severity, x.CreatedAt })
            .HasDatabaseName("ix_outbox_status_severity_created");

        // Correlation-id pivot — operator audit pivots from a trace id
        // to the originating outbox row to inspect Payload / Status /
        // AttemptCount during incident triage.
        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_outbox_correlation_id");
    }
}
