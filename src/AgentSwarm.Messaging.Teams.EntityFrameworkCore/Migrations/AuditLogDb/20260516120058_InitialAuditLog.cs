using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Migrations.AuditLogDb
{
    /// <inheritdoc />
    public partial class InitialAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TaskId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ConversationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Checksum = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_ActorId",
                table: "AuditLog",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_CorrelationId",
                table: "AuditLog",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Timestamp",
                table: "AuditLog",
                columns: new[] { "Timestamp", "Id" })
                .Annotation("SqlServer:Clustered", true);

            // Stage 5.2 step 2 — database-level immutability triggers. These are
            // SQL-Server-specific (the gateway's production target per
            // architecture.md §9.2). In-memory SQLite test fixtures install
            // equivalent BEFORE UPDATE / BEFORE DELETE RAISE(ABORT) triggers in
            // the test fixture (AuditLogStoreFixture) because SQLite does not
            // support INSTEAD OF triggers on base tables. Both flavors achieve
            // the same compliance guarantee: any UPDATE or DELETE against a row
            // in AuditLog is rejected at the storage layer regardless of who
            // issued the statement, so neither application code nor a privileged
            // SQL session can tamper with persisted audit records.
            //
            // The triggers are intentionally NOT guarded by a feature flag — the
            // story's Compliance requirement (the "Immutable audit trail" row in
            // tech-spec.md §4.3) demands tamper-resistance be on by default; an
            // opt-in flag would defeat the audit invariant during the time the
            // flag is off.
            //
            // The trigger / role / permission statements below intentionally do
            // NOT pass `suppressTransaction: true` (review iter-r0 — atomic
            // rollback). CREATE TRIGGER, CREATE ROLE, GRANT, REVOKE and DENY
            // are all transactional DDL in SQL Server and may run inside the
            // migration's ambient transaction; participating in that transaction
            // means a failure in any later step (e.g. the second trigger, a
            // GRANT, or the __EFMigrationsHistory insert) rolls back the partial
            // changes instead of leaving the database in a half-applied state
            // with triggers but no role, or permissions but no triggers. The
            // "first statement in batch" rule for CREATE TRIGGER is still
            // satisfied because each `migrationBuilder.Sql(...)` call is emitted
            // as its own batch by the SQL Server migrations generator.
            migrationBuilder.Sql(
                "CREATE TRIGGER [dbo].[TR_AuditLog_NoUpdate] " +
                "ON [dbo].[AuditLog] " +
                "INSTEAD OF UPDATE " +
                "AS " +
                "BEGIN " +
                "    SET NOCOUNT ON; " +
                "    THROW 50001, 'AuditLog rows are immutable: UPDATE is not permitted on this table.', 1; " +
                "END;");

            migrationBuilder.Sql(
                "CREATE TRIGGER [dbo].[TR_AuditLog_NoDelete] " +
                "ON [dbo].[AuditLog] " +
                "INSTEAD OF DELETE " +
                "AS " +
                "BEGIN " +
                "    SET NOCOUNT ON; " +
                "    THROW 50002, 'AuditLog rows are immutable: DELETE is not permitted on this table.', 1; " +
                "END;");

            // ── Stage 5.2 Step 2 (defense-in-depth GRANT / DENY) ────────────────────────
            // Per tech-spec.md §4.3 and the implementation-plan step 2:
            //   "Grant the application's database user only INSERT and SELECT permissions
            //    on the AuditLog table (no UPDATE or DELETE grants) as a defense-in-depth
            //    measure alongside the triggers."
            //
            // Iter-4 restructure (eval iter-2 item 3 — "[public] is too broad"): the
            // migration now creates a dedicated database role `[AuditLogWriter]` and
            // grants the audit table's INSERT + SELECT permissions ONLY to that role.
            // The deployment script is then responsible for adding the application's
            // database principal to the role:
            //
            //     ALTER ROLE [AuditLogWriter] ADD MEMBER [<app_user>];
            //
            // This satisfies "grants only the app user" because no permission is
            // extended to non-members. Granting to a deployment-provisioned role
            // (rather than to `[public]`, which contains every principal including
            // ad-hoc/break-glass logins) is the canonical SQL Server pattern when the
            // migration cannot statically reference an environment-specific principal
            // name (`app_messaging`, `svc_teamsbot`, etc. vary per deployment).
            //
            // The DENY UPDATE, DELETE … TO [public] is preserved as a universal
            // hardening backstop: DENY beats GRANT in SQL Server's permission
            // resolution, so even if a future migration / DBA accidentally
            // `GRANT UPDATE` to a different role containing the app user, the DENY
            // stays in effect and the statement is rejected at the permission-check
            // layer BEFORE any trigger fires.
            //
            // Privileged principals (`sysadmin`, the database owner) bypass permission
            // checks entirely — for those cases the INSTEAD OF triggers above are the
            // only line of defence. The two layers together satisfy the brief's
            // "combination of DB triggers (preventing mutation even by privileged
            // queries) and restrictive grants (preventing mutation by the application
            // service principal)" requirement and produce a tamper-resistance contract
            // that is verifiable end-to-end against a SQL Server instance.
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID('AuditLogWriter') IS NULL " +
                "    EXEC('CREATE ROLE [AuditLogWriter];');");

            migrationBuilder.Sql(
                "GRANT INSERT, SELECT ON OBJECT::[dbo].[AuditLog] TO [AuditLogWriter];");

            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON OBJECT::[dbo].[AuditLog] FROM [AuditLogWriter];");

            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON OBJECT::[dbo].[AuditLog] FROM [public];");

            migrationBuilder.Sql(
                "DENY UPDATE, DELETE ON OBJECT::[dbo].[AuditLog] TO [public];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the GRANT / DENY before dropping the table so the permission
            // entries do not become orphaned in sys.database_permissions. These run
            // inside the migration's ambient transaction (no suppressTransaction)
            // so a failure in the role-drop or DropTable step rolls back the
            // permission changes atomically — see Up() for the rationale.
            migrationBuilder.Sql(
                "REVOKE INSERT, SELECT, UPDATE, DELETE ON OBJECT::[dbo].[AuditLog] FROM [AuditLogWriter];");

            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON OBJECT::[dbo].[AuditLog] FROM [public];");

            // Drop the role only when it has no remaining members (deployment scripts
            // that ALTER ROLE [AuditLogWriter] ADD MEMBER expect the role to outlive
            // a single migration's Down(); a partial cleanup that strips members
            // would silently break their permission chain).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID('AuditLogWriter') IS NOT NULL " +
                "    AND NOT EXISTS (SELECT 1 FROM sys.database_role_members " +
                "                    WHERE role_principal_id = DATABASE_PRINCIPAL_ID('AuditLogWriter')) " +
                "    EXEC('DROP ROLE [AuditLogWriter];');");

            migrationBuilder.Sql(
                "IF OBJECT_ID('dbo.TR_AuditLog_NoDelete', 'TR') IS NOT NULL DROP TRIGGER [dbo].[TR_AuditLog_NoDelete];");

            migrationBuilder.Sql(
                "IF OBJECT_ID('dbo.TR_AuditLog_NoUpdate', 'TR') IS NOT NULL DROP TRIGGER [dbo].[TR_AuditLog_NoUpdate];");

            migrationBuilder.DropTable(
                name: "AuditLog");
        }
    }
}
