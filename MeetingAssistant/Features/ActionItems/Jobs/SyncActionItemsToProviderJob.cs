using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Services.Abstractions;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.ActionItems.Jobs
{
    public class SyncActionItemsToProviderJob
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ITaskProviderFactory _providerFactory;
        private readonly ILogger<SyncActionItemsToProviderJob> _logger;

        public SyncActionItemsToProviderJob(
            ApplicationDbContext dbContext,
            ITaskProviderFactory providerFactory,
            ILogger<SyncActionItemsToProviderJob> logger)
        {
            _dbContext = dbContext;
            _providerFactory = providerFactory;
            _logger = logger;
        }

        [Hangfire.AutomaticRetry(Attempts = 3)]
        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            List<Guid> actionItemIds,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting sync for {Count} action items in meeting {MeetingId}", actionItemIds.Count, meetingId);

            var integration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId, cancellationToken);

            if (integration == null || integration.Status != IntegrationStatus.Active)
            {
                _logger.LogWarning("Integration not active for org {OrganizationId}", organizationId);
                return;
            }

            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == integration.Type, cancellationToken);

            if (config == null)
            {
                _logger.LogWarning("Integration config not found for org {OrganizationId}", organizationId);
                return;
            }

            var items = await _dbContext.ActionItems
                .Where(x => actionItemIds.Contains(x.Id) && x.MeetingId == meetingId && x.Status == ActionItemStatus.Approved && x.ExternalTaskId == null)
                .ToListAsync(cancellationToken);

            var provider = _providerFactory.GetProvider(integration.Type.ToString());

            foreach (var item in items)
            {
                try
                {
                    string? assigneeExternalId = null;

                    if (item.AssignedToUserId.HasValue)
                    {
                        var accountLink = await _dbContext.ExternalAccountLinks
                            .FirstOrDefaultAsync(l => l.UserId == item.AssignedToUserId.Value && l.OrganizationId == organizationId && l.Provider == integration.Type, cancellationToken);

                        assigneeExternalId = accountLink?.ExternalUserId;

                        if (assigneeExternalId == null)
                        {
                            var mapping = await _dbContext.ExternalMemberMappings
                                .FirstOrDefaultAsync(m => m.UserId == item.AssignedToUserId.Value && m.OrganizationId == organizationId && m.Provider == integration.Type, cancellationToken);

                            assigneeExternalId = mapping?.ExternalMemberId;
                        }
                    }

                    var result = await provider.CreateTaskAsync(config, new ProviderTaskRequest
                    {
                        Title = item.Title,
                        Description = item.Description,
                        DueDateUtc = item.DueDateUtc,
                        AssigneeExternalId = assigneeExternalId
                    }, cancellationToken);

                    item.ExternalTaskId = result.TaskId;
                    item.ExternalTaskUrl = result.TaskUrl;
                    item.ExternalProvider = integration.Type;
                    item.Status = result.HasAssignee ? ActionItemStatus.Synced : ActionItemStatus.SyncedNoAssignee;
                    item.SyncedAtUtc = DateTime.UtcNow;

                    _logger.LogInformation("Synced action item {ActionItemId} to external task {TaskId}", item.Id, result.TaskId);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    integration.Status = IntegrationStatus.NeedsReconnect;
                    _logger.LogError(ex, "Auth failed during sync for org {OrganizationId}", organizationId);
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    integration.Status = IntegrationStatus.InvalidConfig;
                    _logger.LogError(ex, "Config invalid during sync for org {OrganizationId}", organizationId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync action item {ActionItemId}", item.Id);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
