using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationReferences",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AadObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    InternalUserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ChannelId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TeamId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ServiceUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    BotId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConversationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeactivationReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationReferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReferences_AadObjectId_TenantId",
                table: "ConversationReferences",
                columns: new[] { "AadObjectId", "TenantId" },
                unique: true,
                filter: "\"AadObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReferences_ChannelId_TenantId",
                table: "ConversationReferences",
                columns: new[] { "ChannelId", "TenantId" },
                unique: true,
                filter: "\"ChannelId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReferences_InternalUserId_TenantId",
                table: "ConversationReferences",
                columns: new[] { "InternalUserId", "TenantId" },
                filter: "\"InternalUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReferences_IsActive",
                table: "ConversationReferences",
                column: "IsActive",
                filter: "\"IsActive\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReferences_TenantId",
                table: "ConversationReferences",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationReferences");
        }
    }
}
