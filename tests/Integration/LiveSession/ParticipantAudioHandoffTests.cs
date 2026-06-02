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
        public async Task EgressEndedWithMultipleFileResults_ShouldCreateTracksEnqueueTransfers_AndDispatchReadyOnceAfterRoomFinished()
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
            var webhookPublisher = new CollectingPublisher();
            var webhookService = new WebhookService(
                db.DbContext,
                webhookJobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions()),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance,
                publisher: webhookPublisher);

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
            db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId)
                .Should()
                .HaveCount(participantIds.Count)
                .And.OnlyContain(x => x.Status == ParticipantAudioFragmentStatus.Pending);
            webhookJobs.CreatedJobs.Should().HaveCount(participantIds.Count);

            var ingestPublisher = new CollectingPublisher();
            var ingestJob = new IngestParticipantAudioJob(
                db.DbContext,
                ingestPublisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            foreach (var job in webhookJobs.CreatedJobs.Where(x => x.Type == typeof(IngestParticipantAudioJob)))
            {
                var fragmentId = (Guid)job.Args[0];
                var sourceUrl = (string)job.Args[1];
                var sizeBytes = (long?)job.Args[2];
                await ingestJob.RunFragmentAsync(fragmentId, sourceUrl, sizeBytes);
            }

            var completedTracks = db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .ToList();

            completedTracks.Should().HaveCount(participantIds.Count);
            completedTracks.Should().OnlyContain(x => x.Status == ParticipantAudioTrackStatus.Available);
            db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId)
                .Should()
                .HaveCount(participantIds.Count)
                .And.OnlyContain(x => x.Status == ParticipantAudioFragmentStatus.Available);

            db.DbContext.SessionEvents
                .Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantAudioReady)
                .Should()
                .Be(0, "audio readiness waits until the room is completed so late fragments are not missed");

            ingestPublisher.Notifications
                .OfType<ParticipantAudioReadyEvent>()
                .Should()
                .BeEmpty();

            (await webhookService.ProcessAsync(
                WebhookEventFactory.RoomFinished(meetingId, "evt-room-finished-after-egress"),
                "{}"))
                .IsSuccess.Should().BeTrue();

            db.DbContext.SessionEvents
                .Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantAudioReady)
                .Should()
                .Be(1);

            webhookPublisher.Notifications
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

            db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId && x.ParticipantUserId == participantId)
                .OrderBy(x => x.TrackSid)
                .Select(x => x.TrackSid)
                .Should()
                .Equal("TR_AUDIO_1", "TR_AUDIO_2");

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

            db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId && x.ParticipantUserId == participantId)
                .OrderBy(x => x.TrackPublishedAtUtc)
                .Select(x => x.TrackSid)
                .Should()
                .Equal("TR_BEFORE_MUTE", "TR_AFTER_REJOIN");

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

            db.DbContext.ParticipantAudioFragments
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

            db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId)
                .Should()
                .BeEmpty();

            webhookJobs.CreatedJobs.Should().BeEmpty();
        }

        [Fact]
        public async Task EgressEnded_ForPublishedTrack_ShouldUpdateMatchingFragmentAndEnqueueIngestOnce()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("participant");
            db.AddParticipant(meetingId, orgId, participantId);

            var jobs = new FakeBackgroundJobClient();
            var webhookService = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions { EgressHost = "http://egress" }),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            const string trackSid = "TR_SUCCESS_FRAGMENT";
            await webhookService.ProcessAsync(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-success", participantId, trackSid),
                "{}");

            var sourceUrl = $"https://egress.example/bucket/tracks/mtg:{meetingId}/user:{participantId}/track-{trackSid}.ogg";
            var evt = WebhookEventFactory.EgressEnded(
                meetingId,
                "evt-egress-success",
                EgressStatus.EgressComplete,
                participantId,
                sourceUrl);

            var rawPayload = JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    egressId = "EG_success",
                    trackId = trackSid,
                    startedAt = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds(),
                    endedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    fileResults = new[]
                    {
                        new
                        {
                            filename = $"tracks/mtg:{meetingId}/user:{participantId}/track-{trackSid}.ogg",
                            location = sourceUrl,
                            size = 4096L
                        }
                    }
                }
            });

            await webhookService.ProcessAsync(evt, rawPayload);
            await webhookService.ProcessAsync(evt, rawPayload);

            var fragment = db.DbContext.ParticipantAudioFragments
                .Single(x => x.MeetingId == meetingId && x.TrackSid == trackSid);

            fragment.EgressId.Should().Be("EG_success");
            fragment.StorageLocation.Should().Be(sourceUrl);
            fragment.StorageObjectKey.Should().Be($"tracks/mtg:{meetingId}/user:{participantId}/track-{trackSid}.ogg");
            fragment.Status.Should().Be(ParticipantAudioFragmentStatus.Pending);

            jobs.CreatedJobs.Should().ContainSingle();
        }

        [Fact]
        public async Task EgressEnded_FailureForPublishedTrack_ShouldMarkFragmentFailedAndPreserveFailureInfo()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("participant");
            db.AddParticipant(meetingId, orgId, participantId);

            var jobs = new FakeBackgroundJobClient();
            var webhookService = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new MeetingAssistant.Features.LiveSession.Infrastructure.LiveKitOptions { EgressHost = "http://egress" }),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            const string trackSid = "TR_FAILED_FRAGMENT";
            await webhookService.ProcessAsync(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-failed", participantId, trackSid),
                "{}");

            var evt = new WebhookEvent
            {
                Event = "egress_ended",
                Id = "evt-egress-failed",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EgressInfo = new EgressInfo
                {
                    RoomName = $"mtg:{meetingId}",
                    Status = EgressStatus.EgressFailed
                }
            };

            var rawPayload = JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    egressId = "EG_failed",
                    trackId = trackSid,
                    endedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    errorCode = "egress_worker_failed",
                    error = "egress worker terminated before upload"
                }
            });

            await webhookService.ProcessAsync(evt, rawPayload);
            await webhookService.ProcessAsync(evt, rawPayload);

            var fragment = db.DbContext.ParticipantAudioFragments
                .Single(x => x.MeetingId == meetingId && x.TrackSid == trackSid);

            fragment.Status.Should().Be(ParticipantAudioFragmentStatus.Failed);
            fragment.EgressId.Should().Be("EG_failed");
            fragment.FailureCode.Should().Be("egress_worker_failed");
            fragment.FailureMessage.Should().Be("egress worker terminated before upload");
            fragment.FailedAtUtc.Should().NotBeNull();

            db.DbContext.ParticipantAudioTracks
                .Single(x => x.MeetingId == meetingId && x.ParticipantUserId == participantId)
                .Status.Should().Be(ParticipantAudioTrackStatus.Failed);

            jobs.CreatedJobs.Should().BeEmpty();
        }
    }
}
