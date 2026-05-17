// -----------------------------------------------------------------------
// <copyright file="20260601000007_AddProcessedEvents.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 4.3 — adds the <c>processed_events</c> table that backs
    /// <see cref="AgentSwarm.Messaging.Abstractions.IDeduplicationService"/>
    /// via <see cref="PersistentDeduplicationService"/>. One row per
    /// inbound event id; the row's
    /// <see cref="ProcessedEvent.ProcessedAt"/> column distinguishes
    /// the reservation phase (<c>NULL</c>, written by
    /// <see cref="AgentSwarm.Messaging.Abstractions.IDeduplicationService.TryReserveAsync"/>)
    /// from the sticky-processed phase (non-null, written by
    /// <see cref="AgentSwarm.Messaging.Abstractions.IDeduplicationService.MarkProcessedAsync"/>).
    /// Companion <see cref="DeduplicationCleanupService"/> evicts rows
    /// older than the configured TTL on a periodic cadence so the
    /// table stays bounded under burst load.
    /// </summary>
    public partial class AddProcessedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    reserved_at = table.Column<DateTime>(type: "DATETIME", nullable: false),
                    processed_at = table.Column<DateTime>(type: "DATETIME", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_events_processed_reserved",
                table: "processed_events",
                columns: new[] { "processed_at", "reserved_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_events");
        }
    }
}
