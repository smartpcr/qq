using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Adds nullable <c>TenantId</c> and <c>WorkspaceId</c> columns to
    /// the <c>pending_questions</c> table so the callback / timeout
    /// audit paths can persist tenant/workspace context on every
    /// decision row (Stage 5.3 brief: "log every outbound decision
    /// event with full context"). The columns are stamped at
    /// <see cref="PersistentPendingQuestionStore.StoreAsync"/> time by
    /// extracting <c>TenantId</c> / <c>WorkspaceId</c> from
    /// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestionEnvelope.RoutingMetadata"/>
    /// (set upstream by <c>SwarmEventSubscriptionService.StampRouting</c>).
    /// Both columns are nullable so legacy rows persisted before this
    /// migration (and rows for connectors that route without explicit
    /// tenant context) still round-trip cleanly.
    /// </summary>
    /// <remarks>
    /// Provider-portable: only <c>maxLength</c> and <c>nullable</c>
    /// are specified on <see cref="MigrationBuilder.AddColumn{T}"/>,
    /// so EF Core picks the provider-native string mapping at apply
    /// time (SQLite — <c>NVARCHAR(128)</c>; PostgreSQL —
    /// <c>character varying(128)</c>; SQL Server —
    /// <c>nvarchar(128)</c>). No SQLite-specific store-type hint is
    /// pinned, matching the convention of the audit migration in
    /// <c>Migrations/Audit/InitialCreate.cs</c>.
    /// </remarks>
    public partial class AddPendingQuestionTenantWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "pending_questions",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "pending_questions",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "pending_questions");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "pending_questions");
        }
    }
}
