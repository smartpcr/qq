using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationIdHotPathIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConversationReferences_ConversationId",
                table: "ConversationReferences",
                column: "ConversationId",
                filter: "\"IsActive\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationReferences_ConversationId",
                table: "ConversationReferences");
        }
    }
}
