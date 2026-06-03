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
        public void ParseSegments_ShouldCreateSyntheticSegment_WhenJsonContainsTextButNoSegments()
        {
            var participantId = Guid.NewGuid();
            var segments = ParseSegments(participantId, """{"text":"  no segments  "}""", 120_000);

            segments.Should().ContainSingle();
            segments[0].ParticipantUserId.Should().Be(participantId);
            segments[0].StartMs.Should().Be(120_000);
            segments[0].EndMs.Should().Be(120_000);
            segments[0].Text.Should().Be("no segments");
            segments[0].AvgLogProb.Should().BeNull();
        }

        [Fact]
        public void ParseSegments_ShouldCreateSyntheticSegment_WhenJsonContainsTextAndEmptySegmentsArray()
        {
            var participantId = Guid.NewGuid();
            var segments = ParseSegments(participantId, """{"text":"chunk fallback","segments":[]}""", 600_000);

            segments.Should().ContainSingle();
            segments[0].ParticipantUserId.Should().Be(participantId);
            segments[0].StartMs.Should().Be(600_000);
            segments[0].EndMs.Should().Be(600_000);
            segments[0].Text.Should().Be("chunk fallback");
            segments[0].AvgLogProb.Should().BeNull();
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"text\":\"\"}")]
        [InlineData("{\"text\":\"   \"}")]
        [InlineData("{\"text\":\"   \",\"segments\":[]}")]
        public void ParseSegments_ShouldReturnEmpty_WhenNoSegmentsAndTextIsEmpty(string json)
        {
            var segments = ParseSegments(Guid.NewGuid(), json, 120_000);

            segments.Should().BeEmpty();
        }

        [Theory]
        [InlineData("tracks/meeting/alice.wav", "audio/wav")]
        [InlineData("tracks/meeting/alice.mp3", "audio/mpeg")]
        [InlineData("tracks/meeting/alice.ogg", "audio/ogg")]
        [InlineData("tracks/meeting/alice.flac", "audio/flac")]
        [InlineData("tracks/meeting/alice.m4a", "audio/mp4")]
        [InlineData("tracks/meeting/alice", "application/octet-stream")]
        public void ResolveAudioContentType_ShouldMatchStorageObjectExtension(
            string objectKeyOrFileName,
            string expectedContentType)
        {
            ResolveAudioContentType(objectKeyOrFileName).Should().Be(expectedContentType);
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

        private static string ResolveAudioContentType(string objectKeyOrFileName)
        {
            var method = typeof(SttService).GetMethod(
                "ResolveAudioContentType",
                BindingFlags.NonPublic | BindingFlags.Static);

            method.Should().NotBeNull();

            var result = method!.Invoke(null, [objectKeyOrFileName]);

            result.Should().BeOfType<string>();
            return (string)result!;
        }
    }
}
