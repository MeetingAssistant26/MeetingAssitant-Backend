using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalizedSummaryEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SummaryText",
                table: "PersonalizedMeetingSummaries",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "LlmModel",
                table: "PersonalizedMeetingSummaries",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<DateTime>(
                name: "GeneratedAtUtc",
                table: "PersonalizedMeetingSummaries",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<string>(
                name: "EligibilityContextJson",
                table: "PersonalizedMeetingSummaries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EligibilityReason",
                table: "PersonalizedMeetingSummaries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "PersonalizedMeetingSummaries",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("ALTER TABLE \"PersonalizedMeetingSummaries\" ALTER COLUMN \"Status\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EligibilityContextJson",
                table: "PersonalizedMeetingSummaries");

            migrationBuilder.DropColumn(
                name: "EligibilityReason",
                table: "PersonalizedMeetingSummaries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PersonalizedMeetingSummaries");

            migrationBuilder.AlterColumn<string>(
                name: "SummaryText",
                table: "PersonalizedMeetingSummaries",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LlmModel",
                table: "PersonalizedMeetingSummaries",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "GeneratedAtUtc",
                table: "PersonalizedMeetingSummaries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
