using Microsoft.AspNetCore.Identity;

namespace MeetingAssistant.Entities
{
    public sealed class ApplicationUser:IdentityUser
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }

        public string? ProfileAvatarUrl { get; set; }=string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public List<RefreshToken> RefreshTokens { get; set; } = [];
    }
}
