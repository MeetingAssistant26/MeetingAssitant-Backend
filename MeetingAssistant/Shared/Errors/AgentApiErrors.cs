using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace MeetingAssistant.Shared.Errors
{
    public static class AgentApiErrors
    {
        public static readonly Error AccessDenied = new(
            "AgentApi.AccessDenied",
            "Access denied for the current agent scope.",
            StatusCodes.Status403Forbidden);

        public static readonly Error MissingClaims = new(
            "AgentApi.MissingClaims",
            "Required agent claims are missing from the token.",
            StatusCodes.Status403Forbidden);

        public static readonly Error TokenRefreshDenied = new(
            "AgentApi.TokenRefreshDenied",
            "Agent token refresh is only allowed while the meeting is in progress.",
            StatusCodes.Status403Forbidden);

        public static readonly Error InvalidTokenLifetime = new(
            "AgentApi.InvalidTokenLifetime",
            "Token lifetime must be greater than zero.",
            StatusCodes.Status400BadRequest);

        public static readonly Error InvalidMeetingStatusFilter = new(
            "AgentApi.InvalidMeetingStatusFilter",
            "Status filter must be either 'upcoming' or 'past'.",
            StatusCodes.Status400BadRequest);

        public static readonly Error PublicReminderRequired = new(
            "AgentApi.PublicReminderRequired",
            "Only public reminders are supported for this operation.",
            StatusCodes.Status403Forbidden);

        public static readonly Error AgentOwnedReminderRequired = new(
            "AgentApi.AgentOwnedReminderRequired",
            "Only reminders created through the agent channel can be modified by this endpoint.",
            StatusCodes.Status403Forbidden);

        public static readonly Error InvalidReminderScope = new(
            "AgentApi.InvalidReminderScope",
            "Reminder scope must be either Personal or Public.",
            StatusCodes.Status400BadRequest);
    }
}
