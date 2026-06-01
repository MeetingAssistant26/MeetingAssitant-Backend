using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using Microsoft.AspNetCore.Http;

namespace MeetingAssistant.Shared.Errors
{
    public static class MeetingErrors
    {
        public static readonly Error NotFound = new(
            "Meetings.NotFound", 
            "The specified meeting was not found.",
            StatusCodes.Status404NotFound);

        public static readonly Error InvalidStatus = new(
            "Meetings.InvalidStatus", 
            "The operation is invalid for the current meeting status.",
            StatusCodes.Status400BadRequest);

        public static readonly Error InvalidLifecycleTransition = new(
            "Meetings.InvalidLifecycleTransition",
            "The requested meeting lifecycle transition is invalid for the current meeting status.",
            StatusCodes.Status409Conflict);

        public static readonly Error LifecycleControlForbidden = new(
            "Meetings.LifecycleControlForbidden",
            "Caller must be a meeting host, co-host, or organization admin to control the meeting lifecycle.",
            StatusCodes.Status403Forbidden);

        public static readonly Error NotParticipant = new(
            "Meetings.NotParticipant",
            "You are not a participant of this meeting.",
            StatusCodes.Status403Forbidden);

        public static readonly Error NotHost = new(
            "Meetings.NotHost",
            "Caller is not a host of the meeting.",
            StatusCodes.Status403Forbidden);

        public static readonly Error AlreadyParticipant = new(
            "Meetings.AlreadyParticipant",
            "User is already a participant of this meeting.",
            StatusCodes.Status409Conflict);

        public static readonly Error NotOrgMember = new(
            "Meetings.NotOrgMember",
            "User is not a member of the organization.",
            StatusCodes.Status403Forbidden);

        public static readonly Error RemovalForbidden = new(
            "Meetings.RemovalForbidden",
            "CoHosts can only remove Participants and Observers.",
            StatusCodes.Status403Forbidden);

        public static readonly Error RoleUpdateForbidden = new(
            "Meetings.RoleUpdateForbidden",
            "CoHosts can only update Participants and Observers to Participant or Observer roles.",
            StatusCodes.Status403Forbidden);

        public static readonly Error LastHost = new(
            "Meetings.LastHost",
            "Cannot remove the last host of the meeting.",
            StatusCodes.Status403Forbidden);

        public static readonly Error LastHostRoleChange = new(
            "Meetings.LastHostRoleChange",
            "Cannot change the role of the last host of the meeting.",
            StatusCodes.Status403Forbidden);

        public static readonly Error InvalidRecurrence = new(
            "Meetings.InvalidRecurrence",
            "The provided recurrence configuration is invalid.",
            StatusCodes.Status400BadRequest);

        public static readonly Error ConflictDetected = new(
            "Meetings.ConflictDetected",
            "Scheduling conflict detected.",
            StatusCodes.Status409Conflict);

        public static Error ConflictDetectedWithDetails(IReadOnlyCollection<ConflictResponse> conflicts) =>
            ConflictDetected with
            {
                Errors = new Dictionary<string, string[]>
                {
                    ["ScheduledStartUtc"] = ["One or more participants have overlapping meetings."]
                },
                Extensions = new Dictionary<string, object?>
                {
                    ["conflicts"] = conflicts
                }
            };

        public static Error ConflictDetectedWithOccurrenceDetails(
            IReadOnlyCollection<OccurrenceConflictResponse> conflicts,
            int totalOccurrencesChecked) =>
            ConflictDetected with
            {
                Errors = new Dictionary<string, string[]>
                {
                    ["ScheduledStartUtc"] = ["One or more recurring meeting occurrences overlap existing meetings."]
                },
                Extensions = new Dictionary<string, object?>
                {
                    ["recurringConflicts"] = new RecurringConflictCheckResponse(
                        totalOccurrencesChecked,
                        conflicts.Count,
                        conflicts.ToList())
                }
            };

        public static readonly Error GuestNotAllowed = new(
            "Meetings.GuestNotAllowed",
            "Guests are not allowed to create or modify meetings.",
            StatusCodes.Status403Forbidden);

        public static readonly Error ParticipantNotFound = new(
            "Meetings.ParticipantNotFound",
            "The specified participant was not found in the meeting.",
            StatusCodes.Status404NotFound);

        public static readonly Error InvalidParticipantRole = new(
            "Meetings.InvalidParticipantRole",
            "MeetingRole is required and must be a valid role.",
            StatusCodes.Status400BadRequest);

        public static readonly Error TagNotFound = new(
            "Meetings.TagNotFound",
            "One or more specified tags were not found or are inactive.",
            StatusCodes.Status404NotFound);
    }
}
