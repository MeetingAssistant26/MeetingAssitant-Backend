using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class PartialFailureTests
    {
        [Fact]
        public async Task TranscriptAndSummary_ShouldStillBeGenerated_WhenOneTrackTranscriptionFails()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var user1 = db.SeedUser("Alice");
            var user2 = db.SeedUser("Bob");
            var user3 = db.SeedUser("Carol");

            db.AddParticipant(meetingId, orgId, user1);
            db.AddParticipant(meetingId, orgId, user2);
            db.AddParticipant(meetingId, orgId, user3);

            var key1 = $"tracks/{meetingId}/{user1}.ogg";
            var key2 = $"tracks/{meetingId}/{user2}.ogg";
            var key3 = $"tracks/{meetingId}/{user3}.ogg";

            db.DbContext.ParticipantAudioTracks.AddRange(
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = user1,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = key1
                },
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = user2,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = key2
                },
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = user3,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = key3
                });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(
                new Dictionary<string, TrackTranscriptionResult>
                {
                    [key1] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(user1, 0, 1_000, "alice update", 0.95)]),
                    [key3] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(user3, 2_000, 3_000, "carol decision", 0.9)])
                },
                new Dictionary<string, Exception>
                {
                    [key2] = new InvalidOperationException("simulated stt failure")
                });

            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Should().Contain("alice update");
            transcript.FullText.Should().Contain("carol decision");
            transcript.FullText.Should().NotContain("Bob");

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);

            var summarizer = new StubSummarizerService(new SummaryResult(
                "Partial summary generated.",
                "gpt-4o-mini",
                50,
                20));

            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance);

            await summaryJob.RunAsync(meetingId, orgId);

            var summary = db.DbContext.MeetingSummaries.Single(x => x.MeetingId == meetingId);
            summary.SummaryText.Should().Be("Partial summary generated.");
            summarizer.LastTranscript.Should().Be(transcript.FullText);
        }
    }
}
