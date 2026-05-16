using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Persistence.Migrations
{
    /// <summary>
    /// Stage 3.5 — adds the <c>pending_questions</c> table that backs
    /// <see cref="AgentSwarm.Messaging.Abstractions.IPendingQuestionStore"/>
    /// via <see cref="PersistentPendingQuestionStore"/>. One row per
    /// agent question successfully sent to Telegram and awaiting an
    /// operator response; the lifecycle is tracked through
    /// <see cref="AgentSwarm.Messaging.Abstractions.PendingQuestionStatus"/>
    /// (<c>Pending</c> → <c>AwaitingComment</c> → <c>Answered</c> /
    /// <c>TimedOut</c>).
    ///
    /// See architecture.md §3.1 (lines 240–280 for the schema and
    /// constraints) and §10.3 for the <c>QuestionTimeoutService</c>
    /// semantics that read <c>DefaultActionValue</c> directly from
    /// this table (no <c>IDistributedCache</c> dependency).
    /// </summary>
    public partial class AddPendingQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_questions",
                columns: table => new
                {
                    QuestionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AgentQuestionJson = table.Column<string>(type: "TEXT", nullable: false),
                    TelegramChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelegramMessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StoredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DefaultActionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DefaultActionValue = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SelectedActionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SelectedActionValue = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RespondentUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_questions", x => x.QuestionId);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pending_questions_question_id",
                table: "pending_questions",
                column: "QuestionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pending_questions_status_expires_at",
                table: "pending_questions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_questions_default_action_id",
                table: "pending_questions",
                column: "DefaultActionId");

            migrationBuilder.CreateIndex(
                name: "ix_pending_questions_chat_user_status",
                table: "pending_questions",
                columns: new[] { "TelegramChatId", "RespondentUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_questions_chat_message",
                table: "pending_questions",
                columns: new[] { "TelegramChatId", "TelegramMessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_questions");
        }
    }
}
