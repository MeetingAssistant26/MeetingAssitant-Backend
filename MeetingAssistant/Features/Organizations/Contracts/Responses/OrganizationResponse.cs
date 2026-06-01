namespace MeetingAssistant.Features.Organizations.Contracts.Responses
{
    public record OrganizationResponse(
        Guid Id,
        string Name,
        string Slug,
        string Role,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc
    );
}
