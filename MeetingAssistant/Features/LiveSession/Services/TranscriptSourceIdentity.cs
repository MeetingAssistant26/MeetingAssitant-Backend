using System.Security.Cryptography;
using System.Text;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Rag.Models;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public static class TranscriptSourceIdentity
    {
        public static string ComputeHash(string fullText)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fullText ?? string.Empty));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public readonly record struct Identity(
            Guid TranscriptId,
            string TranscriptHash,
            int TranscriptRevision,
            DateTime GeneratedAtUtc);

        public static Identity From(MeetingTranscript transcript)
            => new(
                transcript.Id,
                transcript.TranscriptHash,
                transcript.TranscriptRevision,
                transcript.GeneratedAtUtc);

        public static Identity Resolve(MeetingTranscript transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript.TranscriptHash))
            {
                return new Identity(
                    transcript.Id,
                    ComputeHash(transcript.FullText),
                    Math.Max(1, transcript.TranscriptRevision),
                    transcript.GeneratedAtUtc);
            }

            return From(transcript);
        }

        public static void InitializeNew(MeetingTranscript transcript, string fullText)
        {
            transcript.TranscriptHash = ComputeHash(fullText);
            transcript.TranscriptRevision = 1;
        }

        public static void ApplyContentRevision(MeetingTranscript transcript, string fullText)
        {
            var newHash = ComputeHash(fullText);
            if (string.IsNullOrEmpty(transcript.TranscriptHash))
            {
                transcript.TranscriptHash = newHash;
                transcript.TranscriptRevision = 1;
                return;
            }

            if (string.Equals(transcript.TranscriptHash, newHash, StringComparison.Ordinal))
            {
                return;
            }

            transcript.TranscriptHash = newHash;
            transcript.TranscriptRevision = Math.Max(1, transcript.TranscriptRevision) + 1;
        }

        public static bool MatchesCurrentSource(
            Guid? sourceTranscriptId,
            string? sourceTranscriptHash,
            int? sourceTranscriptRevision,
            Identity current)
        {
            if (string.IsNullOrWhiteSpace(sourceTranscriptHash))
            {
                return false;
            }

            if (!string.Equals(sourceTranscriptHash, current.TranscriptHash, StringComparison.Ordinal))
            {
                return false;
            }

            if (sourceTranscriptRevision.HasValue
                && sourceTranscriptRevision.Value != current.TranscriptRevision)
            {
                return false;
            }

            if (sourceTranscriptId.HasValue && sourceTranscriptId.Value != current.TranscriptId)
            {
                return false;
            }

            return true;
        }

        public static void ApplySourceFields(MeetingSummary summary, Identity identity)
        {
            summary.SourceTranscriptId = identity.TranscriptId;
            summary.SourceTranscriptHash = identity.TranscriptHash;
            summary.SourceTranscriptRevision = identity.TranscriptRevision;
            summary.SourceTranscriptGeneratedAtUtc = identity.GeneratedAtUtc;
        }

        public static void ApplySourceFields(PersonalizedMeetingSummary summary, Identity identity)
        {
            summary.SourceTranscriptId = identity.TranscriptId;
            summary.SourceTranscriptHash = identity.TranscriptHash;
            summary.SourceTranscriptRevision = identity.TranscriptRevision;
            summary.SourceTranscriptGeneratedAtUtc = identity.GeneratedAtUtc;
        }

        public static void ApplySourceFields(ActionItem actionItem, Identity identity)
        {
            actionItem.SourceTranscriptId = identity.TranscriptId;
            actionItem.SourceTranscriptHash = identity.TranscriptHash;
            actionItem.SourceTranscriptRevision = identity.TranscriptRevision;
            actionItem.SourceTranscriptGeneratedAtUtc = identity.GeneratedAtUtc;
        }

        public static void ApplySourceFields(KnowledgeDocument document, Identity identity)
        {
            document.SourceTranscriptId = identity.TranscriptId;
            document.SourceTranscriptHash = identity.TranscriptHash;
            document.SourceTranscriptRevision = identity.TranscriptRevision;
            document.SourceTranscriptGeneratedAtUtc = identity.GeneratedAtUtc;
        }

        public static void ApplySourceFields(KnowledgeChunk chunk, Identity identity)
        {
            chunk.SourceTranscriptId = identity.TranscriptId;
            chunk.SourceTranscriptHash = identity.TranscriptHash;
            chunk.SourceTranscriptRevision = identity.TranscriptRevision;
            chunk.SourceTranscriptGeneratedAtUtc = identity.GeneratedAtUtc;
        }
    }
}
