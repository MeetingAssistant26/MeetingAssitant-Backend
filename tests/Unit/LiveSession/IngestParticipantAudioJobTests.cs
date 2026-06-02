using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.Extensions.Logging.Abstractions;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class IngestParticipantAudioJobTests
    {
        [Fact]
        public async Task RunAsync_ShouldUploadTrackAndMarkAvailable_WhenUploadSucceeds()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");

            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            await db.DbContext.SaveChangesAsync();

            var publisher = new CollectingPublisher();
            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                publisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            await sut.RunAsync(track.Id, $"https://egress.example/bucket/tracks/{meetingId}/{userId}.ogg", 4_096L);

            var updated = db.DbContext.ParticipantAudioTracks.Single(x => x.Id == track.Id);
            updated.Status.Should().Be(ParticipantAudioTrackStatus.Available);
            updated.StorageObjectKey.Should().Be($"tracks/{meetingId}/{userId}.ogg");
            updated.SizeBytes.Should().Be(4_096);
        }

        [Fact]
        public async Task RunAsync_ShouldBeNoOp_OnSecondInvocationForAvailableTrack()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");

            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            await db.DbContext.SaveChangesAsync();

            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                new CollectingPublisher(),
                NullLogger<IngestParticipantAudioJob>.Instance);

            await sut.RunAsync(track.Id, $"https://egress.example/bucket/tracks/{meetingId}/{userId}.ogg", 1024L);
            await sut.RunAsync(track.Id, $"https://egress.example/bucket/tracks/{meetingId}/{userId}-second.ogg", 1024L);
        }

        [Fact]
        public async Task RunAsync_ShouldAllowLongMeetings_WhenTrackIsOlderThanPreviousEightMinuteCeiling()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");

            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            await db.DbContext.SaveChangesAsync();

            track.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30);
            await db.DbContext.SaveChangesAsync();

            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                new CollectingPublisher(),
                NullLogger<IngestParticipantAudioJob>.Instance);

            await sut.RunAsync(track.Id, $"https://egress.example/bucket/tracks/{meetingId}/{userId}.ogg", 1024L);

            var updated = db.DbContext.ParticipantAudioTracks.Single(x => x.Id == track.Id);
            updated.Status.Should().Be(ParticipantAudioTrackStatus.Available);
            updated.StorageObjectKey.Should().Be($"tracks/{meetingId}/{userId}.ogg");
        }

        [Fact]
        public async Task RunFragmentAsync_ShouldAllowLongMeetings_WhenFragmentIsOlderThanPreviousEightMinuteCeiling()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");

            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            };
            var fragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                ParticipantAudioTrack = track,
                TrackSid = "TR_older_than_eight_minutes",
                Status = ParticipantAudioFragmentStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            db.DbContext.ParticipantAudioFragments.Add(fragment);
            await db.DbContext.SaveChangesAsync();

            fragment.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30);
            await db.DbContext.SaveChangesAsync();

            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                new CollectingPublisher(),
                NullLogger<IngestParticipantAudioJob>.Instance);

            await sut.RunFragmentAsync(fragment.Id, $"https://egress.example/bucket/tracks/{meetingId}/{userId}.ogg", 1024L);

            var updated = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragment.Id);
            updated.Status.Should().Be(ParticipantAudioFragmentStatus.Available);
            updated.StorageObjectKey.Should().Be($"tracks/{meetingId}/{userId}.ogg");
            updated.FailureCode.Should().BeNull();
        }

        [Fact]
        public async Task RunAsync_ShouldMarkTrackFailedAndNotThrow_WhenUploadFails()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");

            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            await db.DbContext.SaveChangesAsync();

            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                new CollectingPublisher(),
                NullLogger<IngestParticipantAudioJob>.Instance);

            var act = async () => await sut.RunAsync(track.Id, "not-a-valid-url", 1024L);
            await act.Should().NotThrowAsync();

            var updated = db.DbContext.ParticipantAudioTracks.Single(x => x.Id == track.Id);
            updated.Status.Should().Be(ParticipantAudioTrackStatus.Failed);
            updated.StorageObjectKey.Should().BeNull();
            updated.SizeBytes.Should().BeNull();
        }
    }
}
