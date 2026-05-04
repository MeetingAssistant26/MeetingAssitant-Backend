namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class MemberProviderStatusResponse
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public string? ExternalUsername { get; set; }
        public string? AdminMappedMemberId { get; set; }
    }
}
