namespace MeetingAssistant.Features.ActionItems.Models.Requests
{
    public class SaveProviderCredentialsRequest
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiToken { get; set; } = string.Empty;
    }
}
