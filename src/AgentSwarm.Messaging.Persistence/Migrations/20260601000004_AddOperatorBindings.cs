using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 3.4 — adds the <c>operator_bindings</c> table that records
    /// operator-to-(user, chat, workspace) bindings. Backs
    /// <see cref="AgentSwarm.Messaging.Core.IOperatorRegistry"/> via
    /// <see cref="PersistentOperatorRegistry"/> for runtime
    /// authorization (<c>IsAuthorizedAsync</c> / <c>GetBindingsAsync</c>),
    /// tenant-scoped alias resolution (<c>GetByAliasAsync</c>), alert
    /// fallback routing (<c>GetByWorkspaceAsync</c>), administrative
    /// queries (<c>GetAllBindingsAsync</c>), and the Stage 2.7
    /// subscription service's tenant enumeration
    /// (<c>GetActiveTenantsAsync</c> / <c>GetByTenantAsync</c>).
    ///
    /// See architecture.md §3.1 (lines 105–125 for the schema and
    /// constraints) and implementation-plan.md Stage 3.4 for the
    /// detailed brief.
    /// </summary>
    public partial class AddOperatorBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operator_bindings",
                columns: table => new
                {
                    Id = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelegramChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OperatorAlias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Roles = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_bindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_operator_bindings_user_chat",
                table: "operator_bindings",
                columns: new[] { "TelegramUserId", "TelegramChatId" });

            migrationBuilder.CreateIndex(
                name: "ux_operator_bindings_alias_tenant",
                table: "operator_bindings",
                columns: new[] { "OperatorAlias", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_operator_bindings_user_chat_workspace",
                table: "operator_bindings",
                columns: new[] { "TelegramUserId", "TelegramChatId", "WorkspaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operator_bindings_user",
                table: "operator_bindings",
                column: "TelegramUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operator_bindings");
        }
    }
}
