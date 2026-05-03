namespace MeetingAssistant.Features.Tasks.Contracts.Responses
{
    public sealed record ReminderListResponse(
        List<ReminderResponse> Items,
        int TotalCount,
        int Page,
        int PageSize);
}
