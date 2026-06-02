using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
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
            db.DbContext.ParticipantAudioFragments.Count().Should().Be(1);
            jobs.CreatedJobs.Count.Should().Be(1);
        }

        [Fact]
        public async Task DuplicateEgressEnded_WithDifferentWebhookIdsForSameFile_ShouldEnqueueOnce()
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
                NullLogger<WebhookService>.Instance);

            const string sourceUrl = "https://example.com/bucket/tracks/mtg-room/user-speaker/track.ogg";
            var rawPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    fileResults = new[]
                    {
                        new
                        {
                            filename = $"tracks/mtg-{meetingId}/user:{userId}/file.ogg",
                            location = sourceUrl
                        }
                    }
                }
            });

            await sut.ProcessAsync(
                WebhookEventFactory.EgressEnded(meetingId, "evt-egress-a", Livekit.Server.Sdk.Dotnet.EgressStatus.EgressComplete, userId, sourceUrl),
                rawPayload);
            await sut.ProcessAsync(
                WebhookEventFactory.EgressEnded(meetingId, "evt-egress-b", Livekit.Server.Sdk.Dotnet.EgressStatus.EgressComplete, userId, sourceUrl),
                rawPayload);

            db.DbContext.SessionEvents.Count().Should().Be(1);
            db.DbContext.ParticipantAudioTracks.Count().Should().Be(1);
            db.DbContext.ParticipantAudioFragments.Count().Should().Be(1);
            jobs.CreatedJobs.Count.Should().Be(1);
        }

        [Fact]
        public async Task DuplicateTrackPublished_WithDifferentWebhookIds_ShouldStartEgressOnce()
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
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions { EgressHost = "http://egress" }),
                NullLogger<WebhookService>.Instance);

            await sut.ProcessAsync(WebhookEventFactory.TrackPublished(meetingId, "evt-track-a", userId, "TR_DUPLICATE"), "{}");
            await sut.ProcessAsync(WebhookEventFactory.TrackPublished(meetingId, "evt-track-b", userId, "TR_DUPLICATE"), "{}");

            db.DbContext.SessionEvents
                .Count(x => x.EventType == MeetingAssistant.Features.LiveSession.Models.SessionEventType.TrackPublished)
                .Should()
                .Be(1);
            db.DbContext.ParticipantAudioTracks.Count().Should().Be(1);
            db.DbContext.ParticipantAudioFragments.Count().Should().Be(1);
            jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(StartParticipantAudioEgressJob));
        }
    }
}
