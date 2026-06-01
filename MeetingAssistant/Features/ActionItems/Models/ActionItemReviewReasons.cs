using MeetingAssistant.Features.ActionItems.Models.Entities;

namespace MeetingAssistant.Features.ActionItems.Models
{
    public static class ActionItemReviewReasons
    {
        public const string NeedsAssignee = "NeedsAssignee";
        public const string InvalidDueDate = "InvalidDueDate";

        private const char Separator = ';';

        public static string? From(params string[] reasons)
        {
            var normalized = reasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Select(reason => reason.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return normalized.Length == 0 ? null : string.Join(Separator, normalized);
        }

        public static bool Has(string? persistedReasons, string reason)
        {
            return Split(persistedReasons).Contains(reason, StringComparer.Ordinal);
        }

        public static string? Remove(string? persistedReasons, string reason)
        {
            var remaining = Split(persistedReasons)
                .Where(existing => !string.Equals(existing, reason, StringComparison.Ordinal))
                .ToArray();

            return remaining.Length == 0 ? null : string.Join(Separator, remaining);
        }

        public static bool BlocksApprovalOrSync(ActionItem item)
        {
            if (Has(item.SyncMissingAssigneeReason, InvalidDueDate))
            {
                return true;
            }

            return Has(item.SyncMissingAssigneeReason, NeedsAssignee) && !item.AssignedToUserId.HasValue;
        }

        private static string[] Split(string? persistedReasons)
        {
            return string.IsNullOrWhiteSpace(persistedReasons)
                ? []
                : persistedReasons
                    .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .ToArray();
        }
    }
}
