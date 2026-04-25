using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class RecordingHandoffTests
    {
        [Fact]
        public async Task EgressEndedSuccess_ShouldCreatePendingRecording_AndEnqueueDownload()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();
            db.AddParticipant(meetingId, orgId, userId);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            var evt = WebhookEventFactory.EgressEnded(meetingId, "evt-success", EgressStatus.EgressComplete, userId, "https://example.com/a.mp4");
            await sut.ProcessAsync(evt, "{\"event\":\"egress_ended\"}");

            var recording = db.DbContext.ParticipantAudioTracks.Single(r => r.MeetingId == meetingId);
            recording.Status.Should().Be(MeetingAssistant.Features.LiveSession.Models.ParticipantAudioTrackStatus.Pending);
            jobs.CreatedJobs.Count.Should().Be(1);
        }

        [Fact]
        public async Task DuplicateEgressEnded_ShouldBeIdempotent()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();
            db.AddParticipant(meetingId, orgId, userId);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);
            var evt = WebhookEventFactory.EgressEnded(meetingId, "evt-dup", EgressStatus.EgressComplete, userId, "https://example.com/a.mp4");

            await sut.ProcessAsync(evt, "{}");
            await sut.ProcessAsync(evt, "{}");

            db.DbContext.ParticipantAudioTracks.Count().Should().Be(1);
            jobs.CreatedJobs.Count.Should().Be(1);
        }

        [Fact]
        public async Task EgressEndedFailure_ShouldCreateFailedRecording_AndNotEnqueue()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();
            db.AddParticipant(meetingId, orgId, userId);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            var evt = WebhookEventFactory.EgressEnded(meetingId, "evt-fail", EgressStatus.EgressFailed, userId);
            await sut.ProcessAsync(evt, "{}");

            var recording = db.DbContext.ParticipantAudioTracks.Single(r => r.MeetingId == meetingId);
            recording.Status.Should().Be(MeetingAssistant.Features.LiveSession.Models.ParticipantAudioTrackStatus.Failed);
            jobs.CreatedJobs.Should().BeEmpty();
        }

        [Fact]
        public async Task OrphanRoom_ShouldReturnSuccess_WithoutRecording()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            var evt = WebhookEventFactory.EgressEnded(Guid.NewGuid(), "evt-orphan", EgressStatus.EgressComplete, Guid.NewGuid(), "https://example.com/a.mp4");
            var result = await sut.ProcessAsync(evt, "{}");

            result.IsSuccess.Should().BeTrue();
            db.DbContext.ParticipantAudioTracks.Should().BeEmpty();
        }

        [Fact]
        public void UnsignedPayload_ShouldFailValidation()
        {
            var validator = new LiveKitWebhookValidator(
                Options.Create(new LiveKitOptions
                {
                    ApiKey = "test-key",
                    WebhookSecret = "test-secret"
                }),
                NullLogger<LiveKitWebhookValidator>.Instance);

            var result = validator.Validate("{}", null);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("LiveSession.InvalidWebhookSignature");
        }
    }
}
