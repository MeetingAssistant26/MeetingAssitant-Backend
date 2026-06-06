namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class IntegrationConfigResponse
    {
        public bool HasCredentials { get; set; }
        public bool IsConfigured { get; set; }
        public string IntegrationStatus { get; set; } = string.Empty;
        public string? WorkspaceId { get; set; }
        public string? WorkspaceName { get; set; }
        public string? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public string? ListId { get; set; }
        public string? ListName { get; set; }
    }
}
