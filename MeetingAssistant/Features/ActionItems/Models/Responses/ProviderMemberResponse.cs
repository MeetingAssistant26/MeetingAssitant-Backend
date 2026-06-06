namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class ProviderMemberResponse
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? AvatarUrl { get; set; }
    }
}
