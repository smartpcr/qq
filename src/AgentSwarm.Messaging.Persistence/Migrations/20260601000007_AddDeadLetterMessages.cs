using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadLetterMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stage 4.2 — outbox-row companion dead-letter ledger.
            // Distinct from `outbound_dead_letters` (sender-side ledger
            // keyed on (ChatId, CorrelationId)) — this table is the
            // OutboundQueueProcessor's view, keyed UNIQUE on the
            // OriginalMessageId of the outbox row it dead-lettered.
            migrationBuilder.CreateTable(
                name: "dead_letter_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    SourceEnvelopeJson = table.Column<string>(type: "TEXT", nullable: true),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    AttemptTimestamps = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    ErrorHistory = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    FailureCategory = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    AlertStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AlertSentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReplayStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "None"),
                    ReplayCorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeadLetteredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letter_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_messages_alert_status_severity",
                table: "dead_letter_messages",
                columns: new[] { "AlertStatus", "Severity" });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_messages_correlation_id",
                table: "dead_letter_messages",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_messages_dead_lettered_at",
                table: "dead_letter_messages",
                column: "DeadLetteredAt");

            migrationBuilder.CreateIndex(
                name: "ux_dead_letter_messages_original_message_id",
                table: "dead_letter_messages",
                column: "OriginalMessageId",
                unique: true);

            // Iter-2 evaluator item 4 — AgentId pivot for the
            // "show all dead-letters from <agent>" operator screen.
            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_messages_agent_id",
                table: "dead_letter_messages",
                column: "AgentId");

            // Iter-2 evaluator item 1 — ReplayStatus pivot
            // (architecture.md §3.1 line 399). Operator replay
            // workflow needs to paginate the "replay-eligible"
            // (ReplayStatus = None) and "failed-replay"
            // (ReplayStatus = Failed) views without a full table
            // scan on the dead-letter ledger.
            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_messages_replay_status",
                table: "dead_letter_messages",
                column: "ReplayStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letter_messages");
        }
    }
}
