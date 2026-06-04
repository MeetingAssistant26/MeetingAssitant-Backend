using System;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260604140000_SeparateSttStateFromAudioAvailability")]
    public partial class SeparateSttStateFromAudioAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SttStatus",
                table: "ParticipantAudioFragments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SttAttemptCount",
                table: "ParticipantAudioFragments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSttAttemptAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSttSucceededAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSttFailedAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextSttRetryAtUtc",
                table: "ParticipantAudioFragments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SttFailureCode",
                table: "ParticipantAudioFragments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SttFailureMessage",
                table: "ParticipantAudioFragments",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SttModel",
                table: "ParticipantAudioFragments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SttSegmentCount",
                table: "ParticipantAudioFragments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CompletenessStatus",
                table: "MeetingTranscripts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedAudioFragmentCount",
                table: "MeetingTranscripts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TranscribedAudioFragmentCount",
                table: "MeetingTranscripts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RetryableFailedAudioFragmentCount",
                table: "MeetingTranscripts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TerminalFailedAudioFragmentCount",
                table: "MeetingTranscripts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MissingAudioFragmentIdsJson",
                table: "MeetingTranscripts",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "WarningsJson",
                table: "MeetingTranscripts",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql(
                """
                UPDATE "ParticipantAudioFragments"
                SET "LastSttFailedAtUtc" = "FailedAtUtc",
                    "SttStatus" = 3,
                    "SttAttemptCount" = CASE WHEN "SttAttemptCount" = 0 THEN 1 ELSE "SttAttemptCount" END,
                    "SttFailureCode" = 'stt_failed',
                    "SttFailureMessage" = "FailureMessage",
                    "SttSegmentCount" = 0,
                    "Status" = 2,
                    "FailedAtUtc" = NULL,
                    "FailureCode" = NULL,
                    "FailureMessage" = NULL
                WHERE "Status" = 3
                  AND "FailureCode" = 'stt_failed'
                  AND "StorageObjectKey" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarningsJson",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "MissingAudioFragmentIdsJson",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "TerminalFailedAudioFragmentCount",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "RetryableFailedAudioFragmentCount",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "TranscribedAudioFragmentCount",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "ExpectedAudioFragmentCount",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "CompletenessStatus",
                table: "MeetingTranscripts");

            migrationBuilder.DropColumn(
                name: "SttSegmentCount",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SttModel",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SttFailureMessage",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SttFailureCode",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "NextSttRetryAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "LastSttFailedAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "LastSttSucceededAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "LastSttAttemptAtUtc",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SttAttemptCount",
                table: "ParticipantAudioFragments");

            migrationBuilder.DropColumn(
                name: "SttStatus",
                table: "ParticipantAudioFragments");
        }
    }
}
