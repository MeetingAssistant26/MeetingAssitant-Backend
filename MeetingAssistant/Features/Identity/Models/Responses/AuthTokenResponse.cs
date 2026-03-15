using System;

namespace MeetingAssistant.Features.Identity.Models.Responses
{
    public class AuthTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }
}
