using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Shared.Errors
{
    public static class MeetingTagErrors
    {
        public static readonly Error DuplicateName = new(
            "MeetingTag.DuplicateName",
            "A meeting tag with this name already exists.",
            StatusCodes.Status409Conflict);

        public static readonly Error NotFound = new(
            "MeetingTag.NotFound",
            "The specified meeting tag was not found.",
            StatusCodes.Status404NotFound);
    }
}
