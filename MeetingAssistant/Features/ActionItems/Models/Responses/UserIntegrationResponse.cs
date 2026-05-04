namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class UserIntegrationResponse
    {
        public string Provider { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public string? ExternalUsername { get; set; }
    }
}
