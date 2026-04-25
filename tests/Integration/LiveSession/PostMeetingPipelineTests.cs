using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Handlers;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class PostMeetingPipelineTests
    {
        [Fact]
        public async Task ParticipantAudioReadyPipeline_ShouldWriteTranscriptThenSummary()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var aliceId = db.SeedUser("Alice");
            var bobId = db.SeedUser("Bob");

            db.AddParticipant(meetingId, orgId, aliceId);
            db.AddParticipant(meetingId, orgId, bobId);

            var aliceKey = $"tracks/{meetingId}/{aliceId}.ogg";
            var bobKey = $"tracks/{meetingId}/{bobId}.ogg";

            db.DbContext.ParticipantAudioTracks.AddRange(
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = aliceId,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = aliceKey
                },
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = bobId,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = bobKey
                });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [aliceKey] = new(
                    "whisper-large-v3",
                    new[]
                    {
                        new TranscriptSegment(aliceId, 60_000, 62_000, "action item follow-up", 0.9),
                        new TranscriptSegment(aliceId, 0, 1_500, "project kickoff", 0.95)
                    }),
                [bobKey] = new(
                    "whisper-large-v3",
                    new[]
                    {
                        new TranscriptSegment(bobId, 30_000, 32_000, "timeline update", 0.91)
                    })
            });

            var summarizer = new StubSummarizerService(new SummaryResult(
                "Summary: kickoff, timeline update, action item follow-up.",
                "gpt-4o-mini",
                123,
                45));

            var jobs = new FakeBackgroundJobClient();
            var summaryHandler = new GenerateMeetingSummaryHandler(jobs);
            var publisher = new CollectingPublisher(async (notification, ct) =>
            {
                if (notification is MeetingTranscriptReadyEvent transcriptReadyEvent)
                {
                    await summaryHandler.Handle(transcriptReadyEvent, ct);
                }
            });

            var transcriptHandler = new GenerateMeetingTranscriptHandler(jobs);
            await transcriptHandler.Handle(
                new ParticipantAudioReadyEvent(meetingId, orgId, DateTime.UtcNow),
                CancellationToken.None);

            jobs.CreatedJobs.Should()
                .ContainSingle(x => x.Type == typeof(GenerateMeetingTranscriptJob));

            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            jobs.CreatedJobs.Should()
                .ContainSingle(x => x.Type == typeof(GenerateMeetingSummaryJob));

            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance);

            await summaryJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            var lines = transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None);

            lines.Should().Equal(
                "[00:00:00 Alice] project kickoff",
                "[00:00:30 Bob] timeline update",
                "[00:01:00 Alice] action item follow-up");

            transcript.SttModel.Should().Be("whisper-large-v3");

            var summary = db.DbContext.MeetingSummaries.Single(x => x.MeetingId == meetingId);
            summary.SummaryText.Should().Be("Summary: kickoff, timeline update, action item follow-up.");
            summary.LlmModel.Should().Be("gpt-4o-mini");
            summary.PromptTokens.Should().Be(123);
            summary.CompletionTokens.Should().Be(45);
            summarizer.LastTranscript.Should().Be(transcript.FullText);

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
        }
    }
}
