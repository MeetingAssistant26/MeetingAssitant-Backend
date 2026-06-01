using System.Text.Json;
using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class ParticipantAudioHandoffTests
    {
        [Fact]
        public async Task EgressEndedWithMultipleFileResults_ShouldCreateTracksEnqueueTransfers_AndDispatchReadyOnce()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantIds = Enumerable.Range(0, 3)
                .Select(i => db.SeedUser($"participant-{i + 1}"))
                .ToList();

            foreach (var participantId in participantIds)
            {
                db.AddParticipant(meetingId, orgId, participantId);
            }

            var webhookJobs = new FakeBackgroundJobClient();
            var webhookService = new WebhookService(
                db.DbContext,
                webhookJobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            foreach (var participantId in participantIds)
            {
                var sourceUrl = $"https://egress.example/bucket/tracks/{meetingId}/{participantId}.ogg";
                var evt = new WebhookEvent
                {
                    Event = "egress_ended",
                    Id = $"evt-egress-ended-{participantId}",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    EgressInfo = new EgressInfo
                    {
                        RoomName = $"mtg:{meetingId}",
                        Status = EgressStatus.EgressComplete
                    }
                };

                evt.EgressInfo.FileResults.Add(new Livekit.Server.Sdk.Dotnet.FileInfo
                {
                    Filename = $"tracks/mtg-{meetingId}/user:{participantId}/file.ogg",
                    Location = sourceUrl
                });

                var rawPayload = JsonSerializer.Serialize(new
                {
                    egressInfo = new
                    {
                        fileResults = new[]
                        {
                            new
                            {
                                filename = $"tracks/mtg-{meetingId}/user:{participantId}/file.ogg",
                                location = sourceUrl
                            }
                        }
                    }
                });

                await webhookService.ProcessAsync(evt, rawPayload);
            }

            var pendingTracks = db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .ToList();

            pendingTracks.Should().HaveCount(participantIds.Count);
            pendingTracks.Should().OnlyContain(x => x.Status == ParticipantAudioTrackStatus.Pending);
            webhookJobs.CreatedJobs.Should().HaveCount(participantIds.Count);

            var ingestPublisher = new CollectingPublisher();
            var ingestJob = new IngestParticipantAudioJob(
                db.DbContext,
                ingestPublisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            foreach (var job in webhookJobs.CreatedJobs.Where(x => x.Type == typeof(IngestParticipantAudioJob)))
            {
                var trackId = (Guid)job.Args[0];
                var sourceUrl = (string)job.Args[1];
                var sizeBytes = (long?)job.Args[2];
                await ingestJob.RunAsync(trackId, sourceUrl, sizeBytes);
            }

            var completedTracks = db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .ToList();

            completedTracks.Should().HaveCount(participantIds.Count);
            completedTracks.Should().OnlyContain(x => x.Status == ParticipantAudioTrackStatus.Available);

            db.DbContext.SessionEvents
                .Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantAudioReady)
                .Should()
                .Be(1);

            ingestPublisher.Notifications
                .OfType<ParticipantAudioReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
        }

        [Fact]
        public async Task TrackPublished_MultipleTracksForSameParticipant_ShouldKeepOneAggregateAndStartDistinctEgresses()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("participant");
            db.AddParticipant(meetingId, orgId, participantId);

            var egress = new FakeEgressService();
            var webhookService = new WebhookService(
                db.DbContext,
                new FakeBackgroundJobClient(),
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions { EgressHost = "http://egress" }),
                egress,
                NullLogger<WebhookService>.Instance);

            await webhookService.ProcessAsync(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-1", participantId, "TR_AUDIO_1"),
                "{}");
            await webhookService.ProcessAsync(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-2", participantId, "TR_AUDIO_2"),
                "{}");

            db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId && x.ParticipantUserId == participantId)
                .Should()
                .ContainSingle()
                .Which.Status.Should().Be(ParticipantAudioTrackStatus.Pending);

            db.DbContext.SessionEvents
                .Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.TrackPublished)
                .Should()
                .Be(2);

            egress.Starts.Should().HaveCount(2);
            egress.Starts.Select(x => x.TrackId).Should().BeEquivalentTo("TR_AUDIO_1", "TR_AUDIO_2");
        }

        [Fact]
        public async Task TrackPublished_MuteUnmuteRejoin_ShouldRemainOneParticipantAggregate()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("participant");
            db.AddParticipant(meetingId, orgId, participantId);

            var egress = new FakeEgressService();
            var webhookService = new WebhookService(
                db.DbContext,
                new FakeBackgroundJobClient(),
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions { EgressHost = "http://egress" }),
                egress,
                NullLogger<WebhookService>.Instance);

            await webhookService.ProcessAsync(WebhookEventFactory.ParticipantJoined(meetingId, "evt-join-1", participantId), "{}");
            await webhookService.ProcessAsync(WebhookEventFactory.TrackPublished(meetingId, "evt-track-before-mute", participantId, "TR_BEFORE_MUTE"), "{}");
            await webhookService.ProcessAsync(WebhookEventFactory.ParticipantLeft(meetingId, "evt-left-1", participantId), "{}");
            await webhookService.ProcessAsync(WebhookEventFactory.ParticipantJoined(meetingId, "evt-join-2", participantId), "{}");
            await webhookService.ProcessAsync(WebhookEventFactory.TrackPublished(meetingId, "evt-track-after-rejoin", participantId, "TR_AFTER_REJOIN"), "{}");

            db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId && x.ParticipantUserId == participantId)
                .Should()
                .ContainSingle();

            egress.Starts.Select(x => x.TrackId)
                .Should()
                .BeEquivalentTo("TR_BEFORE_MUTE", "TR_AFTER_REJOIN");

            db.DbContext.SessionEvents.Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantJoined)
                .Should().Be(2);
            db.DbContext.SessionEvents.Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantLeft)
                .Should().Be(1);
        }

        [Fact]
        public async Task TrackPublished_ForUnknownParticipant_ShouldNotCreateAggregateOrStartEgress()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var unknownParticipantId = Guid.NewGuid();

            var egress = new FakeEgressService();
            var webhookService = new WebhookService(
                db.DbContext,
                new FakeBackgroundJobClient(),
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions { EgressHost = "http://egress" }),
                egress,
                NullLogger<WebhookService>.Instance);

            await webhookService.ProcessAsync(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-unknown", unknownParticipantId, "TR_UNKNOWN"),
                "{}");

            db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .Should()
                .BeEmpty();

            egress.Starts.Should().BeEmpty();
        }

        [Fact]
        public async Task EgressEnded_ForUnknownParticipant_ShouldNotCreateAggregateOrEnqueueIngest()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var unknownParticipantId = Guid.NewGuid();

            var webhookJobs = new FakeBackgroundJobClient();
            var webhookService = new WebhookService(
                db.DbContext,
                webhookJobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            var sourceUrl = $"https://egress.example/bucket/tracks/mtg:{meetingId}/user:{unknownParticipantId}/track-unknown.ogg";
            var evt = WebhookEventFactory.EgressEnded(
                meetingId,
                "evt-egress-unknown",
                EgressStatus.EgressComplete,
                unknownParticipantId,
                sourceUrl);

            var rawPayload = JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    fileResults = new[]
                    {
                        new
                        {
                            filename = $"tracks/mtg:{meetingId}/user:{unknownParticipantId}/track-unknown.ogg",
                            location = sourceUrl
                        }
                    }
                }
            });

            await webhookService.ProcessAsync(evt, rawPayload);

            db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .Should()
                .BeEmpty();

            webhookJobs.CreatedJobs.Should().BeEmpty();
        }
    }
}
