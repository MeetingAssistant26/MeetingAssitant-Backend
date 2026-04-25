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

            var storage = new FakeStorageService { NextSizeBytes = 4_096 };
            var publisher = new CollectingPublisher();
            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                storage,
                publisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            await sut.RunAsync(track.Id, "https://egress.example/audio.ogg");

            var updated = db.DbContext.ParticipantAudioTracks.Single(x => x.Id == track.Id);
            updated.Status.Should().Be(ParticipantAudioTrackStatus.Available);
            updated.StorageObjectKey.Should().Be($"tracks/{meetingId}/{userId}.ogg");
            updated.SizeBytes.Should().Be(4_096);

            storage.Uploads.Should().ContainSingle();
            storage.Uploads[0].SourceUrl.Should().Be("https://egress.example/audio.ogg");
            storage.Uploads[0].ObjectKey.Should().Be($"tracks/{meetingId}/{userId}.ogg");
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

            var storage = new FakeStorageService();
            var sut = new IngestParticipantAudioJob(
                db.DbContext,
                storage,
                new CollectingPublisher(),
                NullLogger<IngestParticipantAudioJob>.Instance);

            await sut.RunAsync(track.Id, "https://egress.example/audio.ogg");
            await sut.RunAsync(track.Id, "https://egress.example/audio-second.ogg");

            storage.Uploads.Should().ContainSingle();
            storage.Uploads[0].SourceUrl.Should().Be("https://egress.example/audio.ogg");
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
                new FakeStorageService { ThrowOnUpload = true },
                new CollectingPublisher(),
                NullLogger<IngestParticipantAudioJob>.Instance);

            var act = async () => await sut.RunAsync(track.Id, "https://egress.example/audio.ogg");
            await act.Should().NotThrowAsync();

            var updated = db.DbContext.ParticipantAudioTracks.Single(x => x.Id == track.Id);
            updated.Status.Should().Be(ParticipantAudioTrackStatus.Failed);
            updated.StorageObjectKey.Should().BeNull();
        }
    }
}
