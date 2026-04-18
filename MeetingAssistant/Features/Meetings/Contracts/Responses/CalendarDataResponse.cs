namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record CalendarDataResponse(
        DateTime WeekStartUtc,
        DateTime WeekEndUtc,
        IReadOnlyList<MeetingResponse> Meetings);
}