using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Iter-4 evaluator item 4 — adds the
    /// <c>outbound_dead_letters</c> table that durably records every
    /// outbound Telegram send the sender gave up on after exhausting
    /// the in-sender retry budget. Backs
    /// <see cref="AgentSwarm.Messaging.Abstractions.IOutboundDeadLetterStore"/>
    /// so the operator audit trail of "Telegram send failed
    /// permanently, alert raised, give up" outcomes survives a worker
    /// restart even before Stage 4.1's outbox-row DLQ path lands.
    /// </summary>
    public partial class AddOutboundDeadLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_dead_letters",
                columns: table => new
                {
                    DeadLetterId = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCategory = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    LastErrorType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FailedAt = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_dead_letters", x => x.DeadLetterId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_dlq_correlation_id",
                table: "outbound_dead_letters",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_dlq_chat_id",
                table: "outbound_dead_letters",
                column: "ChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound_dead_letters");
        }
    }
}
