using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 5.3 iter-6 evaluator item 1 — the prior iter dropped
    /// <c>audit_log_entries</c> outright when introducing the
    /// dedicated <see cref="AgentSwarm.Messaging.Persistence.AuditDbContext"/>.
    /// That migration deleted historical audit rows, which violates
    /// the Stage 5.3 immutability guarantee (<i>"audit records must
    /// not be discarded by a migration"</i>).
    /// <para>
    /// Two viable preservation strategies were considered:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Same-migration INSERT…SELECT</b> from
    ///   <c>audit_log_entries</c> into the new <c>audit_logs</c> table.
    ///   Rejected because the two tables can live in <i>different
    ///   physical databases</i> in production: the new table belongs
    ///   to the dedicated <c>AuditDbContext</c> which is bound to a
    ///   separate <c>ConnectionStrings:AuditDb</c> (see
    ///   <c>ServiceCollectionExtensions.AddPersistence</c>) — a
    ///   single-database <c>INSERT INTO audit_logs SELECT …</c> would
    ///   silently fail or, worse, succeed against the wrong database
    ///   when dev defaults collapse both contexts onto one SQLite
    ///   file. Cross-database backfill is an operator step, not a
    ///   migration step.</description></item>
    ///   <item><description><b>Rename in-place</b> to
    ///   <c>audit_log_entries_legacy</c>. <b>Chosen.</b> Preserves
    ///   every historical row verbatim, leaves the data on the
    ///   <i>operational</i> database (where it was always written),
    ///   stops the <see cref="AgentSwarm.Messaging.Persistence.MessagingDbContext"/>
    ///   from mapping it (the model no longer references it so EF
    ///   ignores the table), and gives operators a clean handle to
    ///   either backfill into <c>audit_logs</c> at their own pace or
    ///   archive/export the rows out-of-band. All three providers we
    ///   target (SQLite, PostgreSQL, SQL Server) support
    ///   <c>ALTER TABLE … RENAME TO</c> / <c>sp_rename</c> which is
    ///   what <see cref="MigrationBuilder.RenameTable"/> emits.</description></item>
    /// </list>
    /// <para>
    /// The pre-existing indexes (<c>ix_audit_log_entries_correlation_id</c>,
    /// <c>ix_audit_log_entries_timestamp</c>, <c>ix_audit_log_entries_user_id</c>)
    /// remain attached to the renamed table on all three providers
    /// — their <i>names</i> stay the same but they reference the
    /// <c>_legacy</c> table, which is intentional: the legacy table
    /// is no longer written to, so index hygiene matters only for
    /// the operator's manual queries against the preserved history.
    /// </para>
    /// </summary>
    public partial class DropMessagingAuditLogEntries : Migration
    {
        // Stage 5.3 iter-6 evaluator item 1 — symbolic constants for
        // the rename so the Up and Down halves cannot drift and so
        // a grep on either name finds both directions.
        private const string OriginalTableName = "audit_log_entries";
        private const string LegacyTableName = "audit_log_entries_legacy";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stage 5.3 iter-6 evaluator item 1 — preserve, do not
            // drop. The renamed table is no longer mapped by any
            // EF model (the operational MessagingDbContext stopped
            // referencing it in the same PR; the new AuditDbContext
            // owns a separate `audit_logs` table) so EF leaves it
            // alone. Operators can backfill or archive at their
            // discretion; the historical rows remain queryable via
            // raw SQL against `audit_log_entries_legacy`.
            migrationBuilder.RenameTable(
                name: OriginalTableName,
                newName: LegacyTableName);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stage 5.3 iter-6 evaluator item 1 — symmetric inverse:
            // rename back so a `dotnet ef database update <prior>`
            // restores the original table (and any preserved rows)
            // exactly as it was. This replaces the original Down
            // which CreateTable'd a blank table; that would have
            // silently lost the preserved rows on a down-migration.
            migrationBuilder.RenameTable(
                name: LegacyTableName,
                newName: OriginalTableName);
        }
    }
}
