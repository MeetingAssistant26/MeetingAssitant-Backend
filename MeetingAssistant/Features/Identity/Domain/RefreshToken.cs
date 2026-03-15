using System;

namespace MeetingAssistant.Features.Identity.Entites
{
    public class RefreshToken
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        public string TokenHash { get; set; } = string.Empty;
        
        public Guid UserId { get; set; }
        public ApplicationUser? User { get; set; }
        
        public string FamilyId { get; set; } = string.Empty;
        
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? RevokedAtUtc { get; set; }
        
        public string? ReplacedByTokenHash { get; set; }
        public DateTime? GracePeriodExpiresAtUtc { get; set; }
        
        public bool IsRevoked => RevokedAtUtc.HasValue;
        public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
        public bool IsActive => !IsRevoked && !IsExpired;
    }
}
