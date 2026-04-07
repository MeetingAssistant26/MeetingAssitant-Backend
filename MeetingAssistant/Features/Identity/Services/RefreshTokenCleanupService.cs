using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Identity.Services
{
    public class RefreshTokenCleanupService(ApplicationDbContext dbContext)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task CleanupExpiredTokensAsync(CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);

            await _dbContext.RefreshTokens
                .Where(rt => (rt.RevokedAtUtc != null && rt.RevokedAtUtc < cutoff)
                           || rt.ExpiresAtUtc < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }
}
