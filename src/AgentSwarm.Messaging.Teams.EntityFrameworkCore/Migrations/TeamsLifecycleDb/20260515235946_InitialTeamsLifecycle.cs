using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Migrations.TeamsLifecycleDb
{
    /// <inheritdoc />
    public partial class InitialTeamsLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentQuestions",
                columns: table => new
                {
                    QuestionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TaskId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetUserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TargetChannelId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConversationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AllowedActionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentQuestions", x => x.QuestionId);
                });

            migrationBuilder.CreateTable(
                name: "CardStates",
                columns: table => new
                {
                    QuestionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActivityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ConversationReferenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardStates", x => x.QuestionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentQuestions_ConversationId_Status_Open",
                table: "AgentQuestions",
                columns: new[] { "ConversationId", "Status" },
                filter: "\"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_AgentQuestions_Status_ExpiresAt_Open",
                table: "AgentQuestions",
                columns: new[] { "Status", "ExpiresAt" },
                filter: "\"Status\" = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentQuestions");

            migrationBuilder.DropTable(
                name: "CardStates");
        }
    }
}
