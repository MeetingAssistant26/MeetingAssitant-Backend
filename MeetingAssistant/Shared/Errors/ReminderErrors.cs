using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace MeetingAssistant.Shared.Errors
{
    public static class ReminderErrors
    {
        public static readonly Error NotFound = new(
            "Reminders.NotFound",
            "The specified reminder was not found.",
            StatusCodes.Status404NotFound);

        public static readonly Error NotPersonal = new(
            "Reminders.NotPersonal",
            "Only personal reminders can be modified by users.",
            StatusCodes.Status403Forbidden);

        public static readonly Error NotOwner = new(
            "Reminders.NotOwner",
            "You can only modify your own personal reminders.",
            StatusCodes.Status403Forbidden);

        public static readonly Error AlreadyDelivered = new(
            "Reminders.AlreadyDelivered",
            "Reminder cannot be cancelled because it is already delivered.",
            StatusCodes.Status409Conflict);

        public static readonly Error AlreadyCancelled = new(
            "Reminders.AlreadyCancelled",
            "Reminder is already cancelled.",
            StatusCodes.Status409Conflict);

        public static readonly Error InvalidStatus = new(
            "Reminders.InvalidStatus",
            "The operation is invalid for the current reminder status.",
            StatusCodes.Status409Conflict);
    }
}
