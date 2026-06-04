using System;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260604220000_AddAssistantAudioFragments")]
    public partial class AddAssistantAudioFragments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParticipantIdentity",
                table: "ParticipantAudioFragments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpeakerRole",
                table: "ParticipantAudioFragments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SpeakerDisplayName",
                table: "ParticipantAudioFragments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ParticipantUserId",
                table: "ParticipantAudioFragments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.Sql(
                """
                UPDATE "ParticipantAudioFragments"
                SET "ParticipantIdentity" = 'user:' || "ParticipantUserId"::text
                WHERE "ParticipantIdentity" IS NULL
                  AND "ParticipantUserId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_Meeting_SpeakerRole_PublishedAt",
                table: "ParticipantAudioFragments",
                columns: new[] { "MeetingId", "SpeakerRole", "TrackPublishedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "ParticipantAudioFragments"
                WHERE "SpeakerRole" = 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_ParticipantAudioFragments_Meeting_SpeakerRole_PublishedAt",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "ParticipantIdentity",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SpeakerRole",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SpeakerDisplayName",
                table: "ParticipantAudioFragments");

            migrationBuilder.AlterColumn<Guid>(
                name: "ParticipantUserId",
                table: "ParticipantAudioFragments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
