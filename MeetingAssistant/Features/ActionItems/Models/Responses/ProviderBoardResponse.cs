namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class ProviderBoardResponse
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? WorkspaceId { get; set; }
        public bool Closed { get; set; }
    }
}
