using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Shared.Errors
{
    public static class MeetingTagSuggestionErrors
    {
        public static readonly Error NotFound = new(
            "MeetingTagSuggestions.NotFound",
            "The specified meeting tag suggestion was not found.",
            StatusCodes.Status404NotFound);

        public static readonly Error MeetingNotFound = new(
            "MeetingTagSuggestions.MeetingNotFound",
            "The specified meeting was not found.",
            StatusCodes.Status404NotFound);

        public static readonly Error Forbidden = new(
            "MeetingTagSuggestions.Forbidden",
            "Caller must be a meeting host, co-host, participant, or organization admin for this operation.",
            StatusCodes.Status403Forbidden);

        public static readonly Error ModifyForbidden = new(
            "MeetingTagSuggestions.ModifyForbidden",
            "Caller must be a meeting host, co-host, or organization admin to review meeting tag suggestions.",
            StatusCodes.Status403Forbidden);

        public static readonly Error InvalidTag = new(
            "MeetingTagSuggestions.InvalidTag",
            "One or more selected tags were not found, inactive, or outside this organization.",
            StatusCodes.Status404NotFound);

        public static readonly Error InvalidSuggestionSelection = new(
            "MeetingTagSuggestions.InvalidSuggestionSelection",
            "Selected suggestion IDs must belong to this meeting and match the selected tag IDs.",
            StatusCodes.Status400BadRequest);

        public static readonly Error InvalidState = new(
            "MeetingTagSuggestions.InvalidState",
            "Only pending tag suggestions can be updated or rejected directly.",
            StatusCodes.Status409Conflict);
    }
}
