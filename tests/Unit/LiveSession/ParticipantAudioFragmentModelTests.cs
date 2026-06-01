using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class ParticipantAudioFragmentModelTests
    {
        [Fact]
        public async Task ParticipantAudioFragments_ShouldStoreMultipleRows_ForSameMeetingAndParticipant()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");

            var aggregate = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(aggregate);
            await db.DbContext.SaveChangesAsync();

            var firstPublishedAt = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
            var secondPublishedAt = firstPublishedAt.AddMinutes(5);

            db.DbContext.ParticipantAudioFragments.AddRange(
                new ParticipantAudioFragment
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    ParticipantUserId = userId,
                    ParticipantAudioTrackId = aggregate.Id,
                    TrackSid = "TR_audio_001",
                    EgressId = "EG_audio_001",
                    StorageObjectKey = $"tracks/{meetingId}/{userId}/TR_audio_001.ogg",
                    StorageLocation = "s3://meeting-audio/tracks/fragment-001.ogg",
                    Status = ParticipantAudioFragmentStatus.Available,
                    SizeBytes = 4096,
                    DurationSeconds = 120.5,
                    TrackPublishedAtUtc = firstPublishedAt,
                    EgressStartedAtUtc = firstPublishedAt.AddSeconds(1),
                    EgressEndedAtUtc = firstPublishedAt.AddMinutes(2),
                    StorageAvailableAtUtc = firstPublishedAt.AddMinutes(2).AddSeconds(10)
                },
                new ParticipantAudioFragment
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    ParticipantUserId = userId,
                    ParticipantAudioTrackId = aggregate.Id,
                    TrackSid = "TR_audio_002",
                    EgressId = "EG_audio_002",
                    StorageObjectKey = $"tracks/{meetingId}/{userId}/TR_audio_002.ogg",
                    StorageLocation = "s3://meeting-audio/tracks/fragment-002.ogg",
                    Status = ParticipantAudioFragmentStatus.Failed,
                    TrackPublishedAtUtc = secondPublishedAt,
                    EgressStartedAtUtc = secondPublishedAt.AddSeconds(1),
                    EgressEndedAtUtc = secondPublishedAt.AddMinutes(1),
                    FailedAtUtc = secondPublishedAt.AddMinutes(1).AddSeconds(5),
                    FailureCode = "egress_failed",
                    FailureMessage = "LiveKit egress returned a terminal failure"
                });

            await db.DbContext.SaveChangesAsync();
            db.DbContext.ChangeTracker.Clear();

            var fragments = await db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId && x.ParticipantUserId == userId)
                .OrderBy(x => x.TrackPublishedAtUtc)
                .ToListAsync();

            fragments.Should().HaveCount(2);
            fragments.Select(x => x.TrackSid).Should().Equal("TR_audio_001", "TR_audio_002");
            fragments.Select(x => x.EgressId).Should().Equal("EG_audio_001", "EG_audio_002");
            fragments[0].StorageObjectKey.Should().EndWith("TR_audio_001.ogg");
            fragments[0].StorageLocation.Should().Be("s3://meeting-audio/tracks/fragment-001.ogg");
            fragments[0].SizeBytes.Should().Be(4096);
            fragments[0].DurationSeconds.Should().Be(120.5);
            fragments[1].Status.Should().Be(ParticipantAudioFragmentStatus.Failed);
            fragments[1].FailureCode.Should().Be("egress_failed");
            fragments[1].FailureMessage.Should().Contain("terminal failure");

            var persistedAggregate = await db.DbContext.ParticipantAudioTracks
                .Include(x => x.Fragments)
                .SingleAsync(x => x.Id == aggregate.Id);

            persistedAggregate.Fragments.Should().HaveCount(2);
        }
    }
}
