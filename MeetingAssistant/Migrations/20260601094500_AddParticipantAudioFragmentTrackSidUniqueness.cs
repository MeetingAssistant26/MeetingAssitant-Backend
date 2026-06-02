using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260601094500_AddParticipantAudioFragmentTrackSidUniqueness")]
    public partial class AddParticipantAudioFragmentTrackSidUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_ParticipantAudioFragments_MeetingId_TrackSid",
                table: "ParticipantAudioFragments",
                columns: new[] { "MeetingId", "TrackSid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ParticipantAudioFragments_MeetingId_TrackSid",
                table: "ParticipantAudioFragments");
        }
    }
}
