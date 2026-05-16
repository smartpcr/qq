using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboundUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbound_updates",
                columns: table => new
                {
                    UpdateId = table.Column<long>(type: "INTEGER", nullable: false),
                    RawPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IdempotencyStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    HandlerErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbound_updates", x => x.UpdateId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_updates_status_attempt",
                table: "inbound_updates",
                columns: new[] { "IdempotencyStatus", "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_updates_update_id",
                table: "inbound_updates",
                column: "UpdateId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbound_updates");
        }
    }
}
