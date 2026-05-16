using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Iter-5 evaluator item 3 — adds the
    /// <c>ProcessingStartedAt</c> lease column to <c>inbound_updates</c>
    /// so the recovery sweep's
    /// <c>ReclaimStaleProcessingAsync</c> path can detect orphaned
    /// rows that became stranded in <c>Processing</c> AFTER the host
    /// startup one-shot reset (crash mid-pipeline, swallowed-
    /// cancel-release, etc).
    /// </summary>
    public partial class AddInboundProcessingStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable to remain backward-compatible with rows persisted
            // before this column existed; the reclaim path treats null
            // as "stale" so legacy rows are recoverable on the next
            // sweep tick after the migration runs.
            migrationBuilder.AddColumn<long>(
                name: "ProcessingStartedAt",
                table: "inbound_updates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingStartedAt",
                table: "inbound_updates");
        }
    }
}
