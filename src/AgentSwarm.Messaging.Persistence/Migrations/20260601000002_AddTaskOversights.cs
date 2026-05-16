using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 3.2 — adds the <c>task_oversights</c> table that records
    /// which operator currently has oversight of which task. Backs
    /// <see cref="AgentSwarm.Messaging.Core.ITaskOversightRepository"/>
    /// and the <c>/handoff</c> command flow. One row per task; handoffs
    /// are an UPDATE (or upsert) of the same row.
    /// </summary>
    public partial class AddTaskOversights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_oversights",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OperatorBindingId = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AssignedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_oversights", x => x.TaskId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_oversights_operator",
                table: "task_oversights",
                column: "OperatorBindingId");

            migrationBuilder.CreateIndex(
                name: "ix_task_oversights_task_id",
                table: "task_oversights",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_oversights");
        }
    }
}
