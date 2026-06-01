namespace MeetingAssistant.Features.Organizations.Contracts.Responses
{
    public sealed record OrganizationListResponse(IReadOnlyList<OrganizationResponse> Items);
}
