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
    [Migration("20260515143000_AddRecurringMeetingSeries")]
    public partial class AddRecurringMeetingSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurringMeetingSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ScheduledStartTimeUtc = table.Column<TimeSpan>(type: "interval", nullable: false),
                    ScheduledEndTimeUtc = table.Column<TimeSpan>(type: "interval", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    DaysOfWeek = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringMeetingSeries", x => x.Id);
                });

            migrationBuilder.AddColumn<int>(
                name: "RecurringOccurrenceIndex",
                table: "Meetings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecurringSeriesId",
                table: "Meetings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMeetingSeries_OrganizationId_Status",
                table: "RecurringMeetingSeries",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Meetings_OrgId_RecurringSeriesId_ScheduledStartUtc",
                table: "Meetings",
                columns: new[] { "OrganizationId", "RecurringSeriesId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Meetings_RecurringSeriesId_RecurringOccurrenceIndex",
                table: "Meetings",
                columns: new[] { "RecurringSeriesId", "RecurringOccurrenceIndex" },
                unique: true,
                filter: "\"RecurringSeriesId\" IS NOT NULL AND \"RecurringOccurrenceIndex\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Meetings_RecurringMeetingSeries_RecurringSeriesId",
                table: "Meetings",
                column: "RecurringSeriesId",
                principalTable: "RecurringMeetingSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__LegacyRecurringSeriesBackfill" AS
                SELECT
                    gen_random_uuid() AS "Id",
                    m."Id" AS "MeetingId",
                    m."OrganizationId",
                    m."Title",
                    m."Description",
                    (m."ScheduledStartUtc"::time - TIME '00:00') AS "ScheduledStartTimeUtc",
                    (m."ScheduledEndUtc"::time - TIME '00:00') AS "ScheduledEndTimeUtc",
                    COALESCE((m."RecurrenceConfig" ->> 'Frequency')::integer, 0) AS "Frequency",
                    COALESCE((m."RecurrenceConfig" ->> 'Interval')::integer, 1) AS "Interval",
                    m."RecurrenceConfig" ->> 'DaysOfWeek' AS "DaysOfWeek",
                    (m."RecurrenceConfig" ->> 'EndsAtUtc')::timestamp with time zone AS "EndsAtUtc",
                    COALESCE(host."UserId", '00000000-0000-0000-0000-000000000000'::uuid) AS "CreatedByUserId",
                    m."CreatedAtUtc",
                    m."UpdatedAtUtc"
                FROM "Meetings" AS m
                LEFT JOIN LATERAL (
                    SELECT p."UserId"
                    FROM "MeetingParticipants" AS p
                    WHERE p."MeetingId" = m."Id"
                    ORDER BY CASE WHEN p."MeetingRole" = 0 THEN 0 ELSE 1 END, p."CreatedAtUtc"
                    LIMIT 1
                ) AS host ON TRUE
                WHERE m."RecurrenceConfig" IS NOT NULL;

                INSERT INTO "RecurringMeetingSeries" (
                    "Id", "OrganizationId", "Title", "Description", "ScheduledStartTimeUtc", "ScheduledEndTimeUtc",
                    "Frequency", "Interval", "DaysOfWeek", "EndsAtUtc", "Status", "CreatedByUserId",
                    "CancelledAtUtc", "CreatedAtUtc", "UpdatedAtUtc")
                SELECT
                    "Id", "OrganizationId", "Title", "Description", "ScheduledStartTimeUtc", "ScheduledEndTimeUtc",
                    "Frequency", "Interval", "DaysOfWeek", "EndsAtUtc", 0, "CreatedByUserId",
                    NULL, "CreatedAtUtc", "UpdatedAtUtc"
                FROM "__LegacyRecurringSeriesBackfill";

                UPDATE "Meetings" AS m
                SET "RecurringSeriesId" = b."Id",
                    "RecurringOccurrenceIndex" = 0
                FROM "__LegacyRecurringSeriesBackfill" AS b
                WHERE m."Id" = b."MeetingId";

                DROP TABLE "__LegacyRecurringSeriesBackfill";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Meetings_RecurringMeetingSeries_RecurringSeriesId",
                table: "Meetings");

            migrationBuilder.DropIndex(
                name: "IX_Meetings_OrgId_RecurringSeriesId_ScheduledStartUtc",
                table: "Meetings");

            migrationBuilder.DropIndex(
                name: "IX_Meetings_RecurringSeriesId_RecurringOccurrenceIndex",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "RecurringOccurrenceIndex",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "RecurringSeriesId",
                table: "Meetings");

            migrationBuilder.DropTable(
                name: "RecurringMeetingSeries");
        }
    }
}
