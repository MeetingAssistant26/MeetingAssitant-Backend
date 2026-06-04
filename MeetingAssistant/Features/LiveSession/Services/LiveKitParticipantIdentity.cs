using System.Text.Json;

namespace MeetingAssistant.Features.LiveSession.Services
{
    internal static class LiveKitParticipantIdentity
    {
        public const string AssistantDisplayName = "AI Assistant";

        public static bool IsAssistantIdentity(string? identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            if (identity.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return identity.Contains("meeting-assistant", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAssistantParticipant(string? identity, string? rawPayload)
        {
            if (IsAssistantIdentity(identity))
            {
                return true;
            }

            return TryReadParticipantKind(rawPayload, out var kind) && IsAgentKind(kind);
        }

        public static string SanitizeIdentityForObjectKey(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
            {
                return "unknown-participant";
            }

            var sanitized = identity
                .Replace("/", "-")
                .Replace("\\", "-")
                .Trim();

            return string.IsNullOrWhiteSpace(sanitized) ? "unknown-participant" : sanitized;
        }

        public static string BuildHumanParticipantIdentity(Guid participantUserId)
            => $"user:{participantUserId}";

        private static bool TryReadParticipantKind(string? rawPayload, out string? kind)
        {
            kind = null;
            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(rawPayload);
                if (!document.RootElement.TryGetProperty("participant", out var participant)
                    || participant.ValueKind != JsonValueKind.Object
                    || !participant.TryGetProperty("kind", out var kindElement))
                {
                    return false;
                }

                if (kindElement.ValueKind == JsonValueKind.String)
                {
                    kind = kindElement.GetString();
                    return !string.IsNullOrWhiteSpace(kind);
                }

                if (kindElement.ValueKind == JsonValueKind.Number
                    && kindElement.TryGetInt32(out var kindNumber))
                {
                    kind = kindNumber.ToString();
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            return false;
        }

        private static bool IsAgentKind(string? kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return false;
            }

            if (string.Equals(kind, "AGENT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "KIND_AGENT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return int.TryParse(kind, out var kindNumber) && kindNumber == 4;
        }
    }
}
