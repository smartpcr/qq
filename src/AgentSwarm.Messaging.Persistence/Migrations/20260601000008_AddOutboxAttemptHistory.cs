using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — adds the
    /// <c>AttemptHistoryJson</c> column to the <c>outbox</c> table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox accumulates a per-attempt failure log in this column so
    /// that, when the row is eventually dead-lettered, the
    /// <c>dead_letter_messages</c> row's <c>AttemptTimestamps</c> and
    /// <c>ErrorHistory</c> columns can be projected from the full retry
    /// progression (architecture.md §3.1 lines 386–388) rather than from
    /// only the final error.
    /// </para>
    /// <para>
    /// Kept as a separate migration from
    /// <c>20260601000007_AddDeadLetterMessages</c> so the DLQ-table
    /// migration stays focused on the new table; the outbox ALTER lives
    /// in its own file with a name that explicitly describes the
    /// outbox-side change. Nullable column with no default so existing
    /// rows are unaffected — the first MarkFailedAsync after upgrade
    /// will materialise the JSON array.
    /// </para>
    /// </remarks>
    public partial class AddOutboxAttemptHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttemptHistoryJson",
                table: "outbox",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptHistoryJson",
                table: "outbox");
        }
    }
}
