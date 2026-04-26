using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class LifecycleStillWorksTests
    {
        [Fact]
        public async Task FullMeetingLifecycle_ShouldDeliverExactlyFourLifecycleEventsInOrder()
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
            notifier.ParticipantJoined.Should().ContainSingle().Which.Should().Be((orgId, userId));
            notifier.ParticipantLeft.Should().ContainSingle().Which.Should().Be((orgId, userId));
            notifier.SessionEndedOrgIds.Should().ContainSingle().Which.Should().Be(orgId);

            notifier.SessionStartedOrgIds.Should().HaveCount(1);
            notifier.ParticipantJoined.Should().HaveCount(1);
            notifier.ParticipantLeft.Should().HaveCount(1);
            notifier.SessionEndedOrgIds.Should().HaveCount(1);
        }

        [Fact]
        public async Task LifecycleEvents_ShouldNotLeakAcrossTenantBoundaries()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgA = db.SeedOrganization("orga", db.TenantOrganizationId);
            var orgB = db.SeedOrganization("orgb", Guid.NewGuid());

            var meetingA = db.SeedMeeting(orgA, MeetingStatus.Scheduled);
            var userA = db.SeedUser("user-a");
            db.AddParticipant(meetingA, orgA, userA);

            db.SeedMeeting(orgB, MeetingStatus.Scheduled);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            await sut.ProcessAsync(WebhookEventFactory.RoomStarted(meetingA, "evt-org-a"), "{}");
            await sut.ProcessAsync(WebhookEventFactory.ParticipantJoined(meetingA, "evt-join-a", userA), "{}");
            await sut.ProcessAsync(WebhookEventFactory.ParticipantLeft(meetingA, "evt-left-a", userA), "{}");
            await sut.ProcessAsync(WebhookEventFactory.RoomFinished(meetingA, "evt-finish-a"), "{}");

            notifier.SessionStartedOrgIds.Should().ContainSingle().Which.Should().Be(orgA);
            notifier.SessionStartedOrgIds.Should().NotContain(orgB);
            notifier.ParticipantJoined.Should().ContainSingle().Which.Should().Be((orgA, userA));
            notifier.ParticipantLeft.Should().ContainSingle().Which.Should().Be((orgA, userA));
            notifier.SessionEndedOrgIds.Should().ContainSingle().Which.Should().Be(orgA);
            notifier.SessionEndedOrgIds.Should().NotContain(orgB);
        }
    }
}
