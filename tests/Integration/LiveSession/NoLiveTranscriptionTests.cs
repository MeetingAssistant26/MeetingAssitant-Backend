using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Hubs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class NoLiveTranscriptionTests
    {
        [Fact]
        public async Task FullMeetingLifecycle_ShouldNotEmitAnyTranscriptionOrCaptionEvents()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var userId = db.SeedUser("participant");
            db.AddParticipant(meetingId, orgId, userId);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            await sut.ProcessAsync(WebhookEventFactory.RoomStarted(meetingId, "evt-room-start"), "{}");
            await sut.ProcessAsync(WebhookEventFactory.ParticipantJoined(meetingId, "evt-join", userId), "{}");
            await sut.ProcessAsync(WebhookEventFactory.ParticipantLeft(meetingId, "evt-left", userId), "{}");
            await sut.ProcessAsync(WebhookEventFactory.RoomFinished(meetingId, "evt-room-finish"), "{}");

            notifier.SessionStartedOrgIds.Should().ContainSingle().Which.Should().Be(orgId);
            notifier.ParticipantJoined.Should().ContainSingle();
            notifier.ParticipantLeft.Should().ContainSingle();
            notifier.SessionEndedOrgIds.Should().ContainSingle().Which.Should().Be(orgId);

            var allMethods = typeof(FakeLiveSessionNotifier).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Notify", StringComparison.OrdinalIgnoreCase))
                .ToList();

            allMethods.Should().NotContain(m => m.Name.Contains("Transcription", StringComparison.OrdinalIgnoreCase));
            allMethods.Should().NotContain(m => m.Name.Contains("Caption", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ILiveSessionNotifier_ShouldNotContainTranscriptionOrCaptionMethods()
        {
            var methods = typeof(ILiveSessionNotifier).GetMethods();

            methods.Should().NotContain(m => m.Name.Contains("Transcription", StringComparison.OrdinalIgnoreCase),
                because: "live transcription methods are out of scope per Phase 4.5");
            methods.Should().NotContain(m => m.Name.Contains("Caption", StringComparison.OrdinalIgnoreCase),
                because: "live caption methods are out of scope per Phase 4.5");
        }
    }
}
