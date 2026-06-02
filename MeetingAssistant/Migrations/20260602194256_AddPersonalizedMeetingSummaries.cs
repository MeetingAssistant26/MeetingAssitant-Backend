using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalizedMeetingSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PersonalizedMeetingSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SummaryText = table.Column<string>(type: "text", nullable: false),
                    LlmModel = table.Column<string>(type: "text", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TargetDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PromptName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PersonalizationContextJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalizedMeetingSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalizedMeetingSummaries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonalizedMeetingSummaries_MeetingParticipants_MeetingPar~",
                        column: x => x.MeetingParticipantId,
                        principalTable: "MeetingParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonalizedMeetingSummaries_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizedMeetingSummaries_MeetingId_UserId",
                table: "PersonalizedMeetingSummaries",
                columns: new[] { "MeetingId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizedMeetingSummaries_MeetingParticipantId",
                table: "PersonalizedMeetingSummaries",
                column: "MeetingParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizedMeetingSummaries_OrganizationId",
                table: "PersonalizedMeetingSummaries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizedMeetingSummaries_OrganizationId_MeetingId",
                table: "PersonalizedMeetingSummaries",
                columns: new[] { "OrganizationId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizedMeetingSummaries_OrganizationId_UserId",
                table: "PersonalizedMeetingSummaries",
                columns: new[] { "OrganizationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizedMeetingSummaries_UserId",
                table: "PersonalizedMeetingSummaries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonalizedMeetingSummaries");

        }
    }
}
