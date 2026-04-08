namespace MeetingAssistant.Features.Organizations.Contracts.Responses
{
    public record MeetingTagResponse(
        Guid Id,
        Guid OrganizationId,
        string Name,
        string? Color,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);
}
