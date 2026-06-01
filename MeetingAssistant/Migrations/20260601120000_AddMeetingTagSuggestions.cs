using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingTagSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingTagSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingTagId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagNameSnapshot = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TagColorSnapshot = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LlmModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uuid", nullable: true),
                    SummaryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuggestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingTagSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingTagSuggestions_MeetingTags_MeetingTagId",
                        column: x => x.MeetingTagId,
                        principalTable: "MeetingTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeetingTagSuggestions_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTagSuggestions_MeetingTagId",
                table: "MeetingTagSuggestions",
                column: "MeetingTagId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTagSuggestions_Org_Meeting_Status",
                table: "MeetingTagSuggestions",
                columns: new[] { "OrganizationId", "MeetingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTagSuggestions_Org_Tag_Status",
                table: "MeetingTagSuggestions",
                columns: new[] { "OrganizationId", "MeetingTagId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_MeetingTagSuggestions_Meeting_Tag_Pending",
                table: "MeetingTagSuggestions",
                columns: new[] { "MeetingId", "MeetingTagId", "Status" },
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingTagSuggestions");
        }
    }
}
