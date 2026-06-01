using System;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260531130000_AddAiAssistantTraceEvents")]
    public partial class AddAiAssistantTraceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiAssistantTraceEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TurnId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ParticipantIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    State = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StepType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StepProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StepEndpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    StepModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StepVoice = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    CharactersCount = table.Column<int>(type: "integer", nullable: true),
                    AudioDurationMs = table.Column<int>(type: "integer", nullable: true),
                    RequestPayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResponsePayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAssistantTraceEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiAssistantTraceEvents_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantTraceEvents_MeetingId",
                table: "AiAssistantTraceEvents",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantTraceEvents_OrganizationId",
                table: "AiAssistantTraceEvents",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantTraceEvents_Org_Meeting_OccurredAtUtc",
                table: "AiAssistantTraceEvents",
                columns: new[] { "OrganizationId", "MeetingId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantTraceEvents_Turn_Sequence",
                table: "AiAssistantTraceEvents",
                columns: new[] { "OrganizationId", "MeetingId", "SessionId", "TurnId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAssistantTraceEvents");
        }
    }
}
