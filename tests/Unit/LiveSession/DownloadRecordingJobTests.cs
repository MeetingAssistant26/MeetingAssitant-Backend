using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.Extensions.Logging.Abstractions;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class DownloadRecordingJobTests
    {
        [Theory]
        [InlineData(ParticipantAudioTrackStatus.Available)]
        [InlineData(ParticipantAudioTrackStatus.Failed)]
        public async Task RunAsync_ShouldEarlyExit_ForTerminalStatuses(ParticipantAudioTrackStatus status)
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();

            db.DbContext.ParticipantAudioTracks.Add(new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = status,
                StorageObjectKey = "recordings/existing.mp4"
            });
            await db.DbContext.SaveChangesAsync();

            var storage = new FakeStorageService();
            var sut = new DownloadRecordingJob(db.DbContext, storage, NullLogger<DownloadRecordingJob>.Instance);

            await sut.RunAsync(meetingId, "https://example.com/recording.mp4");

            storage.Uploads.Should().BeEmpty();
        }

        [Fact]
        public async Task RunAsync_ShouldCompleteRecording_WhenUploadSucceeds()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();

            db.DbContext.ParticipantAudioTracks.Add(new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            });
            await db.DbContext.SaveChangesAsync();

            var storage = new FakeStorageService();
            var sut = new DownloadRecordingJob(db.DbContext, storage, NullLogger<DownloadRecordingJob>.Instance);

            await sut.RunAsync(meetingId, "https://example.com/recording.mp4");

            var recording = db.DbContext.ParticipantAudioTracks.Single(r => r.MeetingId == meetingId);
            recording.Status.Should().Be(ParticipantAudioTrackStatus.Available);
            recording.StorageObjectKey.Should().Be($"recordings/{meetingId}.mp4");
            storage.Uploads.Count.Should().Be(1);
        }

        [Fact]
        public async Task RunAsync_ShouldMarkFailed_WhenUploadThrows()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();

            db.DbContext.ParticipantAudioTracks.Add(new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                Status = ParticipantAudioTrackStatus.Pending
            });
            await db.DbContext.SaveChangesAsync();

            var storage = new FakeStorageService { ThrowOnUpload = true };
            var sut = new DownloadRecordingJob(db.DbContext, storage, NullLogger<DownloadRecordingJob>.Instance);

            await sut.RunAsync(meetingId, "https://example.com/recording.mp4");

            var recording = db.DbContext.ParticipantAudioTracks.Single(r => r.MeetingId == meetingId);
            recording.Status.Should().Be(ParticipantAudioTrackStatus.Failed);
        }
    }
}
