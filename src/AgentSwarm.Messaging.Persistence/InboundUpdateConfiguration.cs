// -----------------------------------------------------------------------
// <copyright file="InboundUpdateConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using AgentSwarm.Messaging.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="InboundUpdate"/> — the durable inbox
/// row used to suppress duplicate Telegram webhook deliveries (FR-005, AC-003).
/// </summary>
public sealed class InboundUpdateConfiguration : IEntityTypeConfiguration<InboundUpdate>
{
    public void Configure(EntityTypeBuilder<InboundUpdate> builder)
    {
        builder.ToTable("InboundUpdates");

        // The Telegram update_id is globally unique per bot and is the canonical
        // dedup key. Using it as the primary key gives us the unique-index guard
        // we need for "exactly-once" inbox semantics — no separate index required.
        builder.HasKey(x => x.UpdateId);

        builder.Property(x => x.UpdateId)
            .ValueGeneratedNever();

        builder.Property(x => x.ReceivedAt)
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.Payload)
            .IsRequired();

        // Supporting (non-unique) index for retention sweeps and ordered replay.
        builder.HasIndex(x => x.ReceivedAt);
    }
}
