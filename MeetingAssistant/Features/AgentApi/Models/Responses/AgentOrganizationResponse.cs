namespace MeetingAssistant.Features.AgentApi.Models.Responses
{
    public sealed record AgentOrganizationResponse(
        Guid Id,
        string Name,
        string Slug,
        int MemberCount);
}
