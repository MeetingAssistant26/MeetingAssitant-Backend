using Microsoft.AspNetCore.Identity;

namespace MeetingAssistant.Features.Identity.Entites
{
    public sealed class ApplicationUser : IdentityUser<Guid>
    {
        public string? DisplayName { get; set; }

        public string? ProfileAvatarUrl { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; }

        // Navigation property
        public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    }
}
