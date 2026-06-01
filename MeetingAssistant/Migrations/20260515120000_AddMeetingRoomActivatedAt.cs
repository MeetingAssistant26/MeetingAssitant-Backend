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
    [Migration("20260515120000_AddMeetingRoomActivatedAt")]
    public partial class AddMeetingRoomActivatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RoomActivatedAtUtc",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Meetings" AS m
                SET "RoomActivatedAtUtc" = e."OccurredAtUtc"
                FROM (
                    SELECT "MeetingId", MIN("OccurredAtUtc") AS "OccurredAtUtc"
                    FROM "SessionEvents"
                    WHERE "EventType" = 0
                    GROUP BY "MeetingId"
                ) AS e
                WHERE m."Id" = e."MeetingId"
                  AND m."RoomActivatedAtUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoomActivatedAtUtc",
                table: "Meetings");
        }
    }
}
