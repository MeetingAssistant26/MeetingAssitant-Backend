namespace MeetingAssistant.Api.Infrastructure.Services
{
    public interface IAgentContextProvider
    {
        bool IsAgentRequest { get; }
        Guid? CurrentOrganizationId { get; }
        Guid? CurrentMeetingId { get; }
    }
}
