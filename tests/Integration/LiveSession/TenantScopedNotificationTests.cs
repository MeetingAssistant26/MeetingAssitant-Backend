using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class TenantScopedNotificationTests
    {
        [Fact]
        public async Task Notifications_ShouldUseOwningOrganizationOnly()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgA = db.SeedOrganization("orga", db.TenantOrganizationId);
            var orgB = db.SeedOrganization("orgb", Guid.NewGuid());
            var meetingA = db.SeedMeeting(orgA);
            db.SeedMeeting(orgB);

            var jobs = new FakeBackgroundJobClient();
            var notifier = new FakeLiveSessionNotifier();
            var sut = new WebhookService(db.DbContext, jobs, notifier, NullLogger<WebhookService>.Instance);

            await sut.ProcessAsync(WebhookEventFactory.RoomStarted(meetingA, "evt-org-a"), "{}");

            notifier.SessionStartedOrgIds.Should().ContainSingle().Which.Should().Be(orgA);
            notifier.SessionStartedOrgIds.Should().NotContain(orgB);
        }
    }
}
