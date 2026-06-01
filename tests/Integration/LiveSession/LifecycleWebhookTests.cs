using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class LifecycleWebhookTests
    {
        [Fact]
        public async Task RoomLifecycleAndParticipantEvents_ShouldPersistAndTransition()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var userId = db.SeedUser("participant");
            db.AddParticipant(meetingId, orgId, userId);

            var jobs = new FakeBackgroundJobClient();
            var sut = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            await sut.ProcessAsync(WebhookEventFactory.RoomStarted(meetingId, "evt-room-start"), "{}");
            await sut.ProcessAsync(WebhookEventFactory.ParticipantJoined(meetingId, "evt-join", userId), "{}");
            await sut.ProcessAsync(WebhookEventFactory.ParticipantLeft(meetingId, "evt-left", userId), "{}");
            await sut.ProcessAsync(WebhookEventFactory.RoomFinished(meetingId, "evt-room-finish"), "{}");
            await sut.ProcessAsync(WebhookEventFactory.RoomFinished(meetingId, "evt-room-finish"), "{}");

            var meeting = db.DbContext.Meetings.Single(m => m.Id == meetingId);
            meeting.Status.Should().Be(MeetingStatus.Completed);
            meeting.RoomActivatedAtUtc.Should().NotBeNull();

            var events = db.DbContext.SessionEvents.ToList();
            events.Should().Contain(x => x.EventType == SessionEventType.RoomStarted);
            events.Should().Contain(x => x.EventType == SessionEventType.RoomFinished);
            events.Should().Contain(x => x.EventType == SessionEventType.ParticipantJoined && x.ParticipantUserId == userId);
            events.Should().Contain(x => x.EventType == SessionEventType.ParticipantLeft && x.ParticipantUserId == userId);
            events.Count(x => x.ExternalEventId == "evt-room-finish").Should().Be(1);
        }

        [Fact]
        public async Task RoomStarted_AfterRestStart_ShouldPersistEventButNotMoveActivationTime()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.InProgress);
            var originalActivation = DateTime.UtcNow.AddMinutes(-10);
            var meeting = db.DbContext.Meetings.Single(m => m.Id == meetingId);
            meeting.RoomActivatedAtUtc = originalActivation;
            await db.DbContext.SaveChangesAsync();

            var sut = new WebhookService(
                db.DbContext,
                new FakeBackgroundJobClient(),
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            var webhookEvent = WebhookEventFactory.RoomStarted(meetingId, "evt-room-start-after-rest");
            webhookEvent.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await sut.ProcessAsync(webhookEvent, "{}");

            meeting = db.DbContext.Meetings.Single(m => m.Id == meetingId);
            meeting.Status.Should().Be(MeetingStatus.InProgress);
            meeting.RoomActivatedAtUtc.Should().Be(originalActivation);
            db.DbContext.SessionEvents.Should().ContainSingle(x => x.ExternalEventId == "evt-room-start-after-rest");
        }

        [Fact]
        public async Task RoomFinished_AfterRestEnd_ShouldPersistEventButRemainCompleted()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Completed);

            var sut = new WebhookService(
                db.DbContext,
                new FakeBackgroundJobClient(),
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            await sut.ProcessAsync(WebhookEventFactory.RoomFinished(meetingId, "evt-room-finish-after-rest"), "{}");

            var meeting = db.DbContext.Meetings.Single(m => m.Id == meetingId);
            meeting.Status.Should().Be(MeetingStatus.Completed);
            db.DbContext.SessionEvents.Should().ContainSingle(x => x.ExternalEventId == "evt-room-finish-after-rest");
        }
    }
}
