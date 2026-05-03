namespace MeetingAssistant.Features.AgentApi.Models.Responses
{
    public sealed record AgentMeetingListResponse(
        List<AgentMeetingResponse> Items,
        int TotalCount,
        int Limit,
        int Offset);
}
