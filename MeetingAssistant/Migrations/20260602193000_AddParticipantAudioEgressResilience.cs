using System;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260602193000_AddParticipantAudioEgressResilience")]
    public partial class AddParticipantAudioEgressResilience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BackupStorageAvailableAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupStoragePath",
                table: "ParticipantAudioFragments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EgressStartAttemptCount",
                table: "ParticipantAudioFragments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "EgressStartLeaseExpiresAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEgressStartAttemptAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastStorageUploadAttemptAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StorageUploadAttemptCount",
                table: "ParticipantAudioFragments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StorageUploadLeaseExpiresAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_EgressStartLease",
                table: "ParticipantAudioFragments",
                columns: new[] { "Status", "EgressId", "EgressStartLeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_StorageUploadLease",
                table: "ParticipantAudioFragments",
                columns: new[] { "Status", "BackupStoragePath", "StorageUploadLeaseExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParticipantAudioFragments_EgressStartLease",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropIndex(
                name: "IX_ParticipantAudioFragments_StorageUploadLease",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "BackupStorageAvailableAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "BackupStoragePath",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "EgressStartAttemptCount",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "EgressStartLeaseExpiresAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "LastEgressStartAttemptAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "LastStorageUploadAttemptAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "StorageUploadAttemptCount",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "StorageUploadLeaseExpiresAtUtc",
                table: "ParticipantAudioFragments");
        }
    }
}
