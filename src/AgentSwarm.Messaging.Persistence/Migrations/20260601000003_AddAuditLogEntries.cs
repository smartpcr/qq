using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 3.2 (iter-2 evaluator item 5) — adds the
    /// <c>audit_log_entries</c> table backing
    /// <see cref="AgentSwarm.Messaging.Persistence.PersistentAuditLogger"/>.
    /// Single-table-per-shape discriminator schema (EntryKind: 0=General,
    /// 1=HumanResponse) so both <c>AuditEntry</c> and
    /// <c>HumanResponseAuditEntry</c> rows share storage.
    /// </summary>
    public partial class AddAuditLogEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log_entries",
                columns: table => new
                {
                    EntryId = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    EntryKind = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    QuestionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ActionValue = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log_entries", x => x.EntryId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_correlation_id",
                table: "audit_log_entries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_user_id",
                table: "audit_log_entries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_timestamp",
                table: "audit_log_entries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log_entries");
        }
    }
}
