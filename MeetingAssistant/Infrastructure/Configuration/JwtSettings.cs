namespace MeetingAssistant.Api.Infrastructure.Configuration
{
    public class JwtSettings
    {
        public string? Issuer { get; set; }
        public string? Audience { get; set; }
        public string? SigningKey { get; set; }
        public string[]? IssuerSigningKeys { get; set; }
        public int TokenExpiryMinutes { get; set; }
        public int RefreshTokenExpiryDays { get; set; } = 14;
    }
}
