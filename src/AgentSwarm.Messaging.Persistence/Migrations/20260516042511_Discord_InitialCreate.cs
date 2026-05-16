using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Discord_InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscordInteractions",
                columns: table => new
                {
                    InteractionId = table.Column<long>(type: "INTEGER", nullable: false),
                    InteractionType = table.Column<int>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    RawPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IdempotencyStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordInteractions", x => x.InteractionId);
                });

            migrationBuilder.CreateTable(
                name: "GuildBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelPurpose = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowedRoleIds = table.Column<string>(type: "TEXT", nullable: false),
                    CommandRestrictions = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildBindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboundMessages",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceType = table.Column<int>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    SourceEnvelopeJson = table.Column<string>(type: "TEXT", nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 5),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PlatformMessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "PendingQuestions",
                columns: table => new
                {
                    QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentQuestion = table.Column<string>(type: "TEXT", nullable: false),
                    DiscordChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscordMessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscordThreadId = table.Column<long>(type: "INTEGER", nullable: true),
                    DefaultActionId = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultActionValue = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedActionId = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedActionValue = table.Column<string>(type: "TEXT", nullable: true),
                    RespondentUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    StoredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingQuestions", x => x.QuestionId);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetterMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorReason = table.Column<string>(type: "TEXT", nullable: false),
                    FailedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetterMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadLetterMessages_OutboundMessages_OriginalMessageId",
                        column: x => x.OriginalMessageId,
                        principalTable: "OutboundMessages",
                        principalColumn: "MessageId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_CorrelationId",
                table: "AuditLog",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Platform_Timestamp",
                table: "AuditLog",
                columns: new[] { "Platform", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterMessages_OriginalMessageId_Unique",
                table: "DeadLetterMessages",
                column: "OriginalMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordInteractions_InteractionId_Unique",
                table: "DiscordInteractions",
                column: "InteractionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordInteractions_Status_ReceivedAt",
                table: "DiscordInteractions",
                columns: new[] { "IdempotencyStatus", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildBindings_Guild_Channel_Workspace_Unique",
                table: "GuildBindings",
                columns: new[] { "GuildId", "ChannelId", "WorkspaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildBindings_Guild_Purpose",
                table: "GuildBindings",
                columns: new[] { "GuildId", "ChannelPurpose" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundMessages_IdempotencyKey_Unique",
                table: "OutboundMessages",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundMessages_Status_Severity_NextRetryAt",
                table: "OutboundMessages",
                columns: new[] { "Status", "Severity", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingQuestions_Channel_Message",
                table: "PendingQuestions",
                columns: new[] { "DiscordChannelId", "DiscordMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingQuestions_Status_ExpiresAt",
                table: "PendingQuestions",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "DeadLetterMessages");

            migrationBuilder.DropTable(
                name: "DiscordInteractions");

            migrationBuilder.DropTable(
                name: "GuildBindings");

            migrationBuilder.DropTable(
                name: "PendingQuestions");

            migrationBuilder.DropTable(
                name: "OutboundMessages");
        }
    }
}
