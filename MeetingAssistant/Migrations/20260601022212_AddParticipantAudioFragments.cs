using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddParticipantAudioFragments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ParticipantAudioFragments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantAudioTrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrackSid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EgressId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StorageObjectKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StorageLocation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    TrackPublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EgressStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EgressEndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StorageAvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantAudioFragments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantAudioFragments_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParticipantAudioFragments_ParticipantAudioTracks_Participan~",
                        column: x => x.ParticipantAudioTrackId,
                        principalTable: "ParticipantAudioTracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_EgressId",
                table: "ParticipantAudioFragments",
                column: "EgressId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_Meeting_Participant_PublishedAt",
                table: "ParticipantAudioFragments",
                columns: new[] { "MeetingId", "ParticipantUserId", "TrackPublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_MeetingId_Status",
                table: "ParticipantAudioFragments",
                columns: new[] { "MeetingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_Org_Meeting_Participant",
                table: "ParticipantAudioFragments",
                columns: new[] { "OrganizationId", "MeetingId", "ParticipantUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_ParticipantAudioTrackId",
                table: "ParticipantAudioFragments",
                column: "ParticipantAudioTrackId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioFragments_TrackSid",
                table: "ParticipantAudioFragments",
                column: "TrackSid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParticipantAudioFragments");
        }
    }
}
