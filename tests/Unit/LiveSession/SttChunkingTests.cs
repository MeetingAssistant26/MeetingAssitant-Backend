using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Services;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class SttChunkingTests
    {
        [Fact]
        public void ChunkedResponses_ShouldOffsetSegmentTimestampsAgainstOriginalTrack()
        {
            var participantId = Guid.NewGuid();

            var firstChunk = ParseSegments(participantId, """
                {
                  "segments": [
                    { "start": 0.2, "end": 1.2, "text": "intro", "avg_logprob": -0.4 }
                  ]
                }
                """, 0);

            var secondChunk = ParseSegments(participantId, """
                {
                  "segments": [
                    { "start": 0.5, "end": 2.0, "text": "follow-up", "avg_logprob": -0.3 }
                  ]
                }
                """, 600_000);

            var merged = firstChunk
                .Concat(secondChunk)
                .OrderBy(x => x.StartMs)
                .ToList();

            merged.Should().HaveCount(2);
            merged[0].StartMs.Should().Be(200);
            merged[0].EndMs.Should().Be(1_200);
            merged[1].StartMs.Should().Be(600_500);
            merged[1].EndMs.Should().Be(602_000);
            merged.Should().OnlyContain(x => x.ParticipantUserId == participantId);
        }

        [Fact]
        public void ParseSegments_ShouldReturnEmpty_WhenVerboseJsonContainsNoSegments()
        {
            var segments = ParseSegments(Guid.NewGuid(), """{"text":"no segments"}""", 120_000);

            segments.Should().BeEmpty();
        }

        private static IReadOnlyList<TranscriptSegment> ParseSegments(
            Guid participantUserId,
            string json,
            long offsetMs)
        {
            var method = typeof(SttService).GetMethod(
                "ParseSegments",
                BindingFlags.NonPublic | BindingFlags.Static);

            method.Should().NotBeNull();

            using var document = JsonDocument.Parse(json);
            var result = method!.Invoke(null, [participantUserId, document.RootElement, offsetMs]);

            result.Should().BeAssignableTo<IReadOnlyList<TranscriptSegment>>();
            return (IReadOnlyList<TranscriptSegment>)result!;
        }
    }
}
