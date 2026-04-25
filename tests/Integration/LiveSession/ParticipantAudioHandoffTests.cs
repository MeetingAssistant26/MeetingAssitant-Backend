using System.Text.Json;
using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
            var notifier = new FakeLiveSessionNotifier();
            var webhookService = new WebhookService(
                db.DbContext,
                webhookJobs,
                notifier,
                NullLogger<WebhookService>.Instance);

            var evt = new WebhookEvent
            {
                Event = "egress_ended",
                Id = "evt-egress-ended-multi",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EgressInfo = new EgressInfo
                {
                    RoomName = $"mtg:{meetingId}",
                    Status = EgressStatus.EgressComplete
                }
            };

            var filePayloads = new List<object>();
            foreach (var participantId in participantIds)
            {
                var sourceUrl = $"https://egress.example/{participantId}.ogg";
                evt.EgressInfo.FileResults.Add(new Livekit.Server.Sdk.Dotnet.FileInfo
                {
                    Filename = $"user:{participantId}",
                    Location = sourceUrl
                });

                filePayloads.Add(new
                {
                    participantIdentity = $"user:{participantId}",
                    location = sourceUrl
                });
            }

            var rawPayload = JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    fileResults = filePayloads
                }
            });

            await webhookService.ProcessAsync(evt, rawPayload);

            var pendingTracks = db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .ToList();

            pendingTracks.Should().HaveCount(participantIds.Count);
            pendingTracks.Should().OnlyContain(x => x.Status == ParticipantAudioTrackStatus.Pending);
            webhookJobs.CreatedJobs.Should().HaveCount(participantIds.Count);

            var ingestStorage = new FakeStorageService();
            var ingestPublisher = new CollectingPublisher();
            var ingestJob = new IngestParticipantAudioJob(
                db.DbContext,
                ingestStorage,
                ingestPublisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            foreach (var job in webhookJobs.CreatedJobs.Where(x => x.Type == typeof(IngestParticipantAudioJob)))
            {
                var trackId = (Guid)job.Args[0];
                var sourceUrl = (string)job.Args[1];
                await ingestJob.RunAsync(trackId, sourceUrl);
            }

            var completedTracks = db.DbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId)
                .ToList();

            completedTracks.Should().HaveCount(participantIds.Count);
            completedTracks.Should().OnlyContain(x => x.Status == ParticipantAudioTrackStatus.Available);
            ingestStorage.Uploads.Should().HaveCount(participantIds.Count);

            db.DbContext.SessionEvents
                .Count(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantAudioReady)
                .Should()
                .Be(1);

            ingestPublisher.Notifications
                .OfType<ParticipantAudioReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
        }
    }
}
