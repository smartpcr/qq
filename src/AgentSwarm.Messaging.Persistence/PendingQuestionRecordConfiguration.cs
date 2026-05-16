// -----------------------------------------------------------------------
// <copyright file="PendingQuestionRecordConfiguration.cs" company="Microsoft Corp.">
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
/// Stage 3.5 — EF Core configuration for
/// <see cref="PendingQuestionRecord"/>. Persists the rows backing
/// <see cref="IPendingQuestionStore"/> in the <c>pending_questions</c>
/// SQLite / PostgreSQL / SQL Server table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes (per implementation-plan.md Stage 3.5 step 1 and
/// architecture.md §3.1 "Constraints").</b>
/// <list type="bullet">
///   <item><description><b><c>ux_pending_questions_question_id</c></b> —
///   unique on <see cref="PendingQuestionRecord.QuestionId"/> ensures
///   one row per question (the column is also the primary key so the
///   declaration is logically redundant; pinning the named index
///   matches the brief's explicit "unique question id" requirement and
///   makes the constraint legible to future readers).</description></item>
///   <item><description><b><c>ix_pending_questions_status_expires_at</c></b> —
///   composite on <c>(Status, ExpiresAt)</c>. The polling query in
///   <see cref="Telegram.QuestionTimeoutService"/>
///   selects rows with <see cref="PendingQuestionStatus.Pending"/> or
///   <see cref="PendingQuestionStatus.AwaitingComment"/> whose
///   <see cref="PendingQuestionRecord.ExpiresAt"/> is in the past;
///   leading on <c>Status</c> keeps the scan narrow as the table
///   grows (most rows will be <see cref="PendingQuestionStatus.Answered"/>
///   / <see cref="PendingQuestionStatus.TimedOut"/>).</description></item>
///   <item><description><b><c>ix_pending_questions_default_action_id</c></b> —
///   single-column on <see cref="PendingQuestionRecord.DefaultActionId"/>.
///   Per the workstream brief (Stage 3.5 step 4) the persistent store
///   must support "indexed lookups by QuestionId, ExpiresAt, and
///   DefaultActionId" so analytics queries that fan out the
///   default-action distribution (e.g. "how many timeouts auto-applied
///   <c>skip</c> last 24h?") and the recovery-tooling sweep that
///   reconciles audit gaps for a specific default-action class can
///   filter against an index instead of scanning. Non-unique —
///   thousands of rows share the same DefaultActionId in steady state.</description></item>
///   <item><description><b><c>ix_pending_questions_chat_user_status</c></b> —
///   composite on
///   <c>(TelegramChatId, RespondentUserId, Status)</c>. Used by
///   <see cref="IPendingQuestionStore.GetAwaitingCommentAsync"/> to
///   match a follow-up text reply to the correct
///   <see cref="PendingQuestionStatus.AwaitingComment"/> row. Non-unique
///   — an operator may have multiple awaiting-comment questions in
///   the same chat after rapid button taps; the query selects the
///   <b>oldest</b> by <see cref="PendingQuestionRecord.StoredAt"/> for
///   deterministic tie-breaking (architecture.md §3.1).</description></item>
///   <item><description><b><c>ix_pending_questions_chat_message</c></b> —
///   composite on <c>(TelegramChatId, TelegramMessageId)</c>. Used by
///   <c>QuestionRecoverySweep</c> for backfill correlation and by
///   <see cref="Telegram.QuestionTimeoutService"/> when editing the
///   original Telegram message. Composite because a Telegram
///   <c>message_id</c> is only unique within a chat (architecture.md
///   §3.1).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Time encoding.</b> <see cref="PendingQuestionRecord.StoredAt"/>
/// and <see cref="PendingQuestionRecord.ExpiresAt"/> are stored as
/// Unix milliseconds via the same
/// <see cref="ValueConverter{TModel, TProvider}"/> pattern used by
/// <see cref="OutboundDeadLetterConfiguration"/>,
/// <see cref="OperatorBindingConfiguration"/>, and
/// <see cref="TaskOversightConfiguration"/>.
/// </para>
/// <para>
/// <b>Status encoding.</b> <see cref="PendingQuestionStatus"/> stores
/// as <c>string</c> (the enum value name) rather than the numeric
/// underlying value. The string form keeps a hand-issued SQL query
/// against the SQLite file legible during incident triage —
/// e.g. <c>WHERE Status = 'AwaitingComment'</c> reads naturally where
/// the integer alternative would force the reader to keep a separate
/// enum-to-int mapping in mind. Mirrors
/// <see cref="InboundUpdateConfiguration"/>'s
/// <c>IdempotencyStatus</c> encoding.
/// </para>
/// </remarks>
public sealed class PendingQuestionRecordConfiguration
    : IEntityTypeConfiguration<PendingQuestionRecord>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public void Configure(EntityTypeBuilder<PendingQuestionRecord> builder)
    {
        builder.ToTable("pending_questions");

        builder.HasKey(x => x.QuestionId);

        builder.Property(x => x.QuestionId)
            .HasMaxLength(64)
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(x => x.AgentQuestionJson)
            .IsRequired();

        builder.Property(x => x.TelegramChatId)
            .IsRequired();

        builder.Property(x => x.TelegramMessageId)
            .IsRequired();

        builder.Property(x => x.ExpiresAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.StoredAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.DefaultActionId)
            .HasMaxLength(64);

        builder.Property(x => x.DefaultActionValue)
            .HasMaxLength(128);

        builder.Property(x => x.SelectedActionId)
            .HasMaxLength(64);

        builder.Property(x => x.SelectedActionValue)
            .HasMaxLength(128);

        builder.Property(x => x.RespondentUserId);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        // The primary-key index already covers QuestionId, but pin a
        // named unique index so the constraint is legible against the
        // raw SQL file during incident triage.
        builder.HasIndex(x => x.QuestionId)
            .IsUnique()
            .HasDatabaseName("ux_pending_questions_question_id");

        builder.HasIndex(x => new { x.Status, x.ExpiresAt })
            .HasDatabaseName("ix_pending_questions_status_expires_at");

        // Per workstream brief step 4 — "indexed lookups by
        // QuestionId, ExpiresAt, and DefaultActionId". The
        // QuestionId index is the PK; ExpiresAt is covered by the
        // (Status, ExpiresAt) composite above; the DefaultActionId
        // index below closes the third requirement.
        builder.HasIndex(x => x.DefaultActionId)
            .HasDatabaseName("ix_pending_questions_default_action_id");

        builder.HasIndex(x => new { x.TelegramChatId, x.RespondentUserId, x.Status })
            .HasDatabaseName("ix_pending_questions_chat_user_status");

        builder.HasIndex(x => new { x.TelegramChatId, x.TelegramMessageId })
            .HasDatabaseName("ix_pending_questions_chat_message");
    }
}
