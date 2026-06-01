using Mapster;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Organizations.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Features.Organizations.Services
{
    public class MeetingTagService(
        ApplicationDbContext dbContext,
        ITenantProvider tenantProvider,
        ILogger<MeetingTagService> logger) : IMeetingTagService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ITenantProvider _tenantProvider = tenantProvider;
        private readonly ILogger<MeetingTagService> _logger = logger;

        public async Task<Result<MeetingTagResponse>> CreateAsync(CreateMeetingTagRequest request, CancellationToken ct)
        {
            var orgId = GetOrganizationId();
            var trimmedName = request.Name.Trim();

            var exists = await _dbContext.MeetingTags
                .AnyAsync(t => t.OrganizationId == orgId 
                            && t.Name.ToLower() == trimmedName.ToLower() 
                            && t.IsActive, ct);

            if (exists)
                return Result.Failure<MeetingTagResponse>(MeetingTagErrors.DuplicateName);

            var tag = new MeetingTag
            {
                OrganizationId = orgId,
                Name = trimmedName,
                Color = MeetingTagColor.Normalize(request.Color),
                IsActive = true
            };

            tag.RaiseDomainEvent(new MeetingTagCreatedEvent(orgId, tag.Id));

            _dbContext.MeetingTags.Add(tag);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting tag {TagId} created for organization {OrgId}", tag.Id, orgId);

            return Result.Success(tag.Adapt<MeetingTagResponse>());
        }

        public async Task<Result<MeetingTagResponse>> UpdateAsync(Guid tagId, UpdateMeetingTagRequest request, CancellationToken ct)
        {
            var orgId = GetOrganizationId();

            var tag = await _dbContext.MeetingTags
                .FirstOrDefaultAsync(t => t.Id == tagId && t.OrganizationId == orgId && t.IsActive, ct);

            if (tag == null)
                return Result.Failure<MeetingTagResponse>(MeetingTagErrors.NotFound);

            if (request.Name != null)
            {
                var trimmedName = request.Name.Trim();

                if (!string.Equals(tag.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
                {
                    var exists = await _dbContext.MeetingTags
                        .AnyAsync(t => t.OrganizationId == orgId 
                                    && t.Id != tagId 
                                    && t.Name.ToLower() == trimmedName.ToLower() 
                                    && t.IsActive, ct);

                    if (exists)
                        return Result.Failure<MeetingTagResponse>(MeetingTagErrors.DuplicateName);
                }

                tag.Name = trimmedName;
            }

            if (request.Color.HasValue)
                tag.Color = MeetingTagColor.Normalize(request.Color.Value);

            tag.RaiseDomainEvent(new MeetingTagUpdatedEvent(orgId, tag.Id));
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting tag {TagId} updated for organization {OrgId}", tag.Id, orgId);

            return Result.Success(tag.Adapt<MeetingTagResponse>());
        }

        public async Task<Result> DeleteAsync(Guid tagId, CancellationToken ct)
        {
            var orgId = GetOrganizationId();

            var tag = await _dbContext.MeetingTags
                .FirstOrDefaultAsync(t => t.Id == tagId && t.OrganizationId == orgId && t.IsActive, ct);

            if (tag == null)
                return Result.Failure(MeetingTagErrors.NotFound);

            tag.IsActive = false;

            tag.RaiseDomainEvent(new MeetingTagDeletedEvent(orgId, tag.Id));
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting tag {TagId} deleted for organization {OrgId}", tag.Id, orgId);

            return Result.Success();
        }

        public async Task<Result<IEnumerable<MeetingTagResponse>>> ListAsync(CancellationToken ct)
        {
            var tags = await _dbContext.MeetingTags
                .Where(t => t.IsActive)
                .OrderBy(t => t.CreatedAtUtc)
                .ToListAsync(ct);

            return Result.Success(tags.Adapt<IEnumerable<MeetingTagResponse>>());
        }

        private Guid GetOrganizationId()
        {
            return _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");
        }
    }
}
