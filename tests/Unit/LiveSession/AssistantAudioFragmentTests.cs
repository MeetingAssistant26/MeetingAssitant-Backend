using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class AssistantAudioFragmentTests
    {
        [Fact]
        public async Task ParticipantAudioFragments_ShouldAllowAssistantRows_WithNullableParticipantUserId()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("speaker");
            db.AddParticipant(meetingId, orgId, userId);

            db.DbContext.ParticipantAudioFragments.AddRange(
                new ParticipantAudioFragment
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SpeakerRole = ParticipantAudioFragmentSpeakerRole.Participant,
                    ParticipantUserId = userId,
                    ParticipantIdentity = $"user:{userId}",
                    TrackSid = "TR_human",
                    Status = ParticipantAudioFragmentStatus.Available,
                    StorageObjectKey = $"tracks/mtg-{meetingId}/user:{userId}/track-TR_human.ogg"
                },
                new ParticipantAudioFragment
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SpeakerRole = ParticipantAudioFragmentSpeakerRole.Assistant,
                    ParticipantUserId = null,
                    ParticipantIdentity = "agent-AJ_test",
                    SpeakerDisplayName = "AI Assistant",
                    TrackSid = "TR_agent",
                    Status = ParticipantAudioFragmentStatus.Available,
                    StorageObjectKey = $"tracks/mtg-{meetingId}/agent-AJ_test/track-TR_agent.ogg"
                });

            await db.DbContext.SaveChangesAsync();
            db.DbContext.ChangeTracker.Clear();

            var fragments = await db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId)
                .OrderBy(x => x.SpeakerRole)
                .ToListAsync();

            fragments.Should().HaveCount(2);
            fragments[0].ParticipantUserId.Should().Be(userId);
            fragments[1].ParticipantUserId.Should().BeNull();
            fragments[1].SpeakerRole.Should().Be(ParticipantAudioFragmentSpeakerRole.Assistant);
            fragments[1].ParticipantIdentity.Should().Be("agent-AJ_test");
        }

        [Fact]
        public async Task TrackPublished_ForAgent_ShouldCreateAssistantFragment_AndEnqueueEgressStart()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var jobs = new FakeBackgroundJobClient();
            var sut = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new LiveKitOptions { EgressHost = "http://egress" }),
                NullLogger<WebhookService>.Instance);

            const string agentIdentity = "agent-AJ_webhook";
            const string trackSid = "TR_AGENT_AUDIO";
            await sut.ProcessAsync(
                WebhookEventFactory.TrackPublishedForAssistant(meetingId, "evt-agent-track", agentIdentity, trackSid),
                """{"participant":{"identity":"agent-AJ_webhook","kind":"AGENT"}}""");

            var fragment = db.DbContext.ParticipantAudioFragments.Single();
            fragment.SpeakerRole.Should().Be(ParticipantAudioFragmentSpeakerRole.Assistant);
            fragment.ParticipantUserId.Should().BeNull();
            fragment.ParticipantIdentity.Should().Be(agentIdentity);
            fragment.SpeakerDisplayName.Should().Be("AI Assistant");
            fragment.ParticipantAudioTrackId.Should().BeNull();
            fragment.TrackSid.Should().Be(trackSid);
            jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(StartParticipantAudioEgressJob));
        }

        [Fact]
        public async Task EgressEnded_ForAgent_ShouldUpdateAssistantFragment_WithoutParticipantUserId()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            const string agentIdentity = "agent-AJ_egress";
            const string trackSid = "TR_AGENT_EGRESS";
            db.DbContext.ParticipantAudioFragments.Add(new ParticipantAudioFragment
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                SpeakerRole = ParticipantAudioFragmentSpeakerRole.Assistant,
                ParticipantIdentity = agentIdentity,
                SpeakerDisplayName = "AI Assistant",
                TrackSid = trackSid,
                Status = ParticipantAudioFragmentStatus.Pending,
                TrackPublishedAtUtc = DateTime.UtcNow
            });
            await db.DbContext.SaveChangesAsync();

            var jobs = new FakeBackgroundJobClient();
            var sut = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new LiveKitOptions()),
                NullLogger<WebhookService>.Instance);

            var sourceUrl = $"https://example.com/recordings/tracks/mtg:{meetingId}/{agentIdentity}/track-{trackSid}.ogg";
            var rawPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    fileResults = new[]
                    {
                        new
                        {
                            filename = $"tracks/mtg-{meetingId}/{agentIdentity}/track-{trackSid}.ogg",
                            location = sourceUrl
                        }
                    }
                }
            });

            await sut.ProcessAsync(
                WebhookEventFactory.EgressEnded(meetingId, "evt-agent-egress", EgressStatus.EgressComplete, null, sourceUrl),
                rawPayload);

            var fragment = db.DbContext.ParticipantAudioFragments.Single();
            fragment.ParticipantUserId.Should().BeNull();
            fragment.SpeakerRole.Should().Be(ParticipantAudioFragmentSpeakerRole.Assistant);
            fragment.StorageLocation.Should().Be(sourceUrl);
            db.DbContext.ParticipantAudioTracks.Should().BeEmpty();
            jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(IngestParticipantAudioJob));
        }

        [Fact]
        public async Task StartParticipantAudioEgressJob_ShouldUseParticipantIdentity_ForAssistantFragment()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            const string agentIdentity = "agent-AJ_start";
            const string trackSid = "TR_AGENT_START";
            var fragmentId = Guid.Empty;
            db.DbContext.ParticipantAudioFragments.Add(new ParticipantAudioFragment
            {
                Id = fragmentId = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                SpeakerRole = ParticipantAudioFragmentSpeakerRole.Assistant,
                ParticipantIdentity = agentIdentity,
                SpeakerDisplayName = "AI Assistant",
                TrackSid = trackSid,
                Status = ParticipantAudioFragmentStatus.Pending,
                TrackPublishedAtUtc = DateTime.UtcNow
            });
            await db.DbContext.SaveChangesAsync();

            var egress = new FakeEgressService();
            var job = new StartParticipantAudioEgressJob(
                db.DbContext,
                egress,
                NullLogger<StartParticipantAudioEgressJob>.Instance);

            await job.RunAsync(fragmentId);

            egress.Starts.Should().ContainSingle();
            egress.Starts[0].ParticipantIdentity.Should().Be(agentIdentity);
        }
    }
}
