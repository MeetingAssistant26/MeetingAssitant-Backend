namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record MeetingListResponse(
        List<MeetingResponse> Items,
        int TotalCount,
        int Page,
        int PageSize);
}