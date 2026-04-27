using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            var sut = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            var evt = WebhookEventFactory.EgressEnded(
                meetingId,
                "evt-1",
                EgressStatus.EgressComplete,
                userId,
                "https://example.com/recording.ogg");

            var rawPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                event_name = "egress_ended",
                egressInfo = new
                {
                    fileResults = new[]
                    {
                        new
                        {
                            filename = $"tracks/mtg-{meetingId}/user:{userId}/file.ogg",
                            location = "https://example.com/recording.ogg"
                        }
                    }
                }
            });

            await sut.ProcessAsync(evt, rawPayload);
            await sut.ProcessAsync(evt, rawPayload);

            db.DbContext.SessionEvents.Count().Should().Be(1);
            db.DbContext.ParticipantAudioTracks.Count().Should().Be(1);
            jobs.CreatedJobs.Count.Should().Be(1);
        }
    }
}
