namespace MeetingAssistant.Features.Meetings.Contracts.Requests;

public sealed record MeetingConflictCheckRequest(
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    List<Guid>? ParticipantUserIds,
    Guid? ExcludeMeetingId);
