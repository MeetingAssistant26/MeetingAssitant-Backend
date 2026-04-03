using System.Text.RegularExpressions;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Organizations.Services
{
    public partial class SlugGenerator(ApplicationDbContext dbContext) : ISlugGenerator
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken cancellationToken = default)
        {
            var baseSlug = Slugify(name);
            var slug = baseSlug;
            var attempt = 0;
            const int maxAttempts = 10;

            while (await _dbContext.Organizations.AnyAsync(o => o.Slug == slug, cancellationToken))
            {
                attempt++;
                if (attempt >= maxAttempts)
                    throw new InvalidOperationException($"Could not generate a unique slug after {maxAttempts} attempts.");
                slug = $"{baseSlug}-{attempt}";
            }

            return slug;
        }

        private static string Slugify(string name)
        {
            var slug = name.ToLowerInvariant().Trim();
            slug = InvalidCharsRegex().Replace(slug, "");
            slug = WhitespaceRegex().Replace(slug, "-");
            slug = MultiDashRegex().Replace(slug, "-");
            slug = slug.Trim('-');
            return string.IsNullOrEmpty(slug) ? "org" : slug[..Math.Min(slug.Length, 100)];
        }

        [GeneratedRegex(@"[^a-z0-9\s-]")]
        private static partial Regex InvalidCharsRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();

        [GeneratedRegex(@"-{2,}")]
        private static partial Regex MultiDashRegex();
    }
}
