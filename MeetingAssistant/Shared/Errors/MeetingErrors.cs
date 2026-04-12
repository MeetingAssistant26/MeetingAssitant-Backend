using MeetingAssistant.Shared.Abstractions;
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

        public static readonly Error LastHost = new(
            "Meetings.LastHost",
            "Cannot remove the last host of the meeting.",
            StatusCodes.Status403Forbidden);

        public static readonly Error InvalidRecurrence = new(
            "Meetings.InvalidRecurrence",
            "The provided recurrence configuration is invalid.",
            StatusCodes.Status400BadRequest);

        public static readonly Error ConflictDetected = new(
            "Meetings.ConflictDetected",
            "A conflicting meeting exists.",
            StatusCodes.Status409Conflict);

        public static readonly Error GuestNotAllowed = new(
            "Meetings.GuestNotAllowed",
            "Guests are not allowed to create or modify meetings.",
            StatusCodes.Status403Forbidden);

        public static readonly Error ParticipantNotFound = new(
            "Meetings.ParticipantNotFound",
            "The specified participant was not found in the meeting.",
            StatusCodes.Status404NotFound);

        public static readonly Error TagNotFound = new(
            "Meetings.TagNotFound",
            "One or more specified tags were not found or are inactive.",
            StatusCodes.Status404NotFound);
    }
}