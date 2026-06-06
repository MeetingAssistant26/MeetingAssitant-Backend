namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class ProviderWorkspaceResponse
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Url { get; set; }
    }
}
