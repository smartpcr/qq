// -----------------------------------------------------------------------
// <copyright file="OutboundMessageIdMappingConfiguration.cs" company="Microsoft Corp.">
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
/// EF Core configuration for <see cref="OutboundMessageIdMapping"/> —
/// the durable Telegram <c>message_id</c> → <c>CorrelationId</c> reverse
/// index that satisfies the iter-3 evaluator item 3 ("message-id to
/// correlation-id tracking must be durable, not best-effort cache").
/// </summary>
/// <remarks>
/// <para>
/// <b>Table name.</b> <c>outbound_message_id_mappings</c> — matches the
/// snake-case convention used by <c>inbound_updates</c> so a single
/// retention sweep can iterate the messaging tables uniformly.
/// </para>
/// <para>
/// <b>Primary key (iter-4 evaluator item 1).</b> Composite
/// (<see cref="OutboundMessageIdMapping.ChatId"/>,
/// <see cref="OutboundMessageIdMapping.TelegramMessageId"/>). Telegram
/// only guarantees uniqueness of <c>message_id</c> WITHIN a single
/// chat — message_id=42 in chat A and message_id=42 in chat B are
/// independent — so a single-column key on
/// <see cref="OutboundMessageIdMapping.TelegramMessageId"/> would let
/// the second send overwrite (or be rejected as duplicate of) the
/// first. The inbound reply path always carries both
/// <c>Message.Chat.Id</c> and <c>Message.ReplyToMessage.MessageId</c>
/// so the composite seek is always satisfiable.
/// </para>
/// <para>
/// <b>SentAt column type.</b> Stored as a Unix-millisecond
/// <see cref="long"/> using the same converter pattern as
/// <c>inbound_updates</c> so SQLite can ORDER BY natively — required
/// for the future retention sweep that prunes rows older than the
/// retention window.
/// </para>
/// </remarks>
public sealed class OutboundMessageIdMappingConfiguration
    : IEntityTypeConfiguration<OutboundMessageIdMapping>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public void Configure(EntityTypeBuilder<OutboundMessageIdMapping> builder)
    {
        builder.ToTable("outbound_message_id_mappings");

        // Iter-4 evaluator item 1 — composite PK. Telegram message_id
        // is only unique within a chat, so the table key MUST include
        // the chat id or two different chats with a colliding id will
        // either overwrite each other (last-write-wins) or fail an
        // INSERT with a phantom-duplicate constraint violation.
        builder.HasKey(x => new { x.ChatId, x.TelegramMessageId });

        builder.Property(x => x.TelegramMessageId)
            .ValueGeneratedNever();

        builder.Property(x => x.ChatId)
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.SentAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        // Secondary index on CorrelationId — supports the dual-direction
        // lookup ("given a trace id, what Telegram messages did we send
        // under it?") that the Stage 5.x operator-audit screens want.
        // The index is non-unique because a single agent question can
        // span multiple split chunks (Stage 2.3 step 162) that each
        // receive their own Telegram message id but share the same
        // correlation id.
        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_outbound_msgid_correlation_id");
    }
}
