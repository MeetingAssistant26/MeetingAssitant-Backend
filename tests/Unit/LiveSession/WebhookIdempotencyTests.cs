using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class WebhookIdempotencyTests
    {
        [Fact]
        public async Task DuplicateExternalEventId_ShouldPersistOnce_AndEnqueueOnce()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser();
            db.AddParticipant(meetingId, orgId, userId);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            var evt = WebhookEventFactory.EgressEnded(
                meetingId,
                "evt-1",
                EgressStatus.EgressComplete,
                userId,
                "https://example.com/recording.mp4");

            await sut.ProcessAsync(evt, "{\"event\":\"egress_ended\"}");
            await sut.ProcessAsync(evt, "{\"event\":\"egress_ended\"}");

            db.DbContext.SessionEvents.Count().Should().Be(1);
            db.DbContext.ParticipantAudioTracks.Count().Should().Be(1);
            jobs.CreatedJobs.Count.Should().Be(1);
        }
    }
}
