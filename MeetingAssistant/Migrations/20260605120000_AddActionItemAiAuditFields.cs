using System;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260605120000_AddActionItemAiAuditFields")]
    public partial class AddActionItemAiAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiAssigneeResolutionReason",
                table: "ActionItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AiAssigneeConfidence",
                table: "ActionItems",
                type: "numeric(5,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiDeadlineResolutionReason",
                table: "ActionItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AiDeadlineConfidence",
                table: "ActionItems",
                type: "numeric(5,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiRawAssigneeText",
                table: "ActionItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiRawDeadlineText",
                table: "ActionItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AiSuggestedAssignedToParticipantId",
                table: "ActionItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AiSuggestedAssignedToUserId",
                table: "ActionItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AiSuggestedDueDateUtc",
                table: "ActionItems",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiAssigneeResolutionReason",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiAssigneeConfidence",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiDeadlineResolutionReason",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiDeadlineConfidence",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiRawAssigneeText",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiRawDeadlineText",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiSuggestedAssignedToParticipantId",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiSuggestedAssignedToUserId",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "AiSuggestedDueDateUtc",
                table: "ActionItems");
        }
    }
}
