using System;

namespace MeetingAssistant.Features.Identity.Models.Responses
{
    public record AuthTokenResponse
    (
        string AccessToken,
        string RefreshToken,
        DateTime ExpiresAtUtc,
        int ExpiresIn
    );
}
