using System.ComponentModel.DataAnnotations;
using Hangfire;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Features.ActionItems.Services.Abstractions;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.ActionItems.Services
{
    public class ActionItemService : IActionItemService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ITaskProviderFactory _providerFactory;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<ActionItemService> _logger;

        public ActionItemService(
            ApplicationDbContext dbContext,
            ITaskProviderFactory providerFactory,
            IBackgroundJobClient backgroundJobClient,
            ILogger<ActionItemService> logger)
        {
            _dbContext = dbContext;
            _providerFactory = providerFactory;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
        }

        public async Task<Result<ActionItemListResponse>> GetByMeetingAsync(
            Guid meetingId, Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var isParticipant = await _dbContext.MeetingParticipants
                .AnyAsync(p => p.MeetingId == meetingId && p.UserId == userId, cancellationToken);

            if (!isParticipant)
                return Result.Failure<ActionItemListResponse>(new Error("Forbidden", "You are not a participant of this meeting.", 403));

            var items = await _dbContext.ActionItems
                .AsNoTracking()
                .Where(x => x.MeetingId == meetingId)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => new ActionItemResponse
                {
                    Id = x.Id,
                    MeetingId = x.MeetingId,
                    Title = x.Title,
                    Description = x.Description,
                    AssignedToUserId = x.AssignedToUserId,
                    AssignedToUserName = x.AssignedToUser != null ? x.AssignedToUser.UserName : null,
                    DueDateUtc = x.DueDateUtc,
                    Status = x.Status.ToString(),
                    ExternalTaskId = x.ExternalTaskId,
                    ExternalTaskUrl = x.ExternalTaskUrl,
                    ExternalProvider = x.ExternalProvider.ToString(),
                    SyncMissingAssigneeReason = x.SyncMissingAssigneeReason,
                    ExtractedAtUtc = x.ExtractedAtUtc,
                    SyncedAtUtc = x.SyncedAtUtc
                })
                .ToListAsync(cancellationToken);

            return Result.Success(new ActionItemListResponse { Items = items });
        }

        public async Task<Result<ActionItemResponse>> UpdateAsync(
            Guid id, UpdateActionItemRequest request,
            Guid userId, Guid organizationId, string etag,
            CancellationToken cancellationToken = default)
        {
            var item = await _dbContext.ActionItems
                .Include(x => x.AssignedToUser)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            if (item == null)
                return Result.Failure<ActionItemResponse>(new Error("NotFound", "Action item not found.", 404));

            if (!await CanModifyAsync(item.MeetingId, userId, organizationId, cancellationToken))
                return Result.Failure<ActionItemResponse>(new Error("Forbidden", "You do not have permission to modify this action item.", 403));

            var etagValidation = ValidateEtag(item, etag);
            if (etagValidation.IsFailure)
                return Result.Failure<ActionItemResponse>(etagValidation.Error);

            if (item.Status == ActionItemStatus.Synced || item.Status == ActionItemStatus.SyncedNoAssignee)
                return Result.Failure<ActionItemResponse>(new Error("Conflict", "Action item has already been synced and is immutable.", 409));

            if (request.Title != null) item.Title = request.Title;
            if (request.Description != null) item.Description = request.Description;
            if (request.AssignedToParticipantId.HasValue)
            {
                item.AssignedToParticipantId = request.AssignedToParticipantId;
                var participant = await _dbContext.MeetingParticipants
                    .FirstOrDefaultAsync(p => p.Id == request.AssignedToParticipantId, cancellationToken);
                item.AssignedToUserId = participant?.UserId;
            }
            if (request.DueDateUtc.HasValue) item.DueDateUtc = request.DueDateUtc;
            if (request.Status != null && Enum.TryParse<ActionItemStatus>(request.Status, out var status))
            {
                if (IsValidTransition(item.Status, status))
                    item.Status = status;
                else
                    return Result.Failure<ActionItemResponse>(new Error("Conflict", $"Invalid status transition from {item.Status} to {status}.", 409));
            }

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<ActionItemResponse>(new Error("Conflict", "The action item was modified by another user. Please refresh and try again.", 409));
            }

            return Result.Success(MapToResponse(item));
        }

        public async Task<Result<ActionItemResponse>> ApproveAsync(
            Guid id, Guid userId, Guid organizationId, string etag,
            CancellationToken cancellationToken = default)
        {
            return await UpdateStatusAsync(id, userId, organizationId, etag, ActionItemStatus.Approved, cancellationToken);
        }

        public async Task<Result<ActionItemResponse>> RejectAsync(
            Guid id, Guid userId, Guid organizationId, string etag,
            CancellationToken cancellationToken = default)
        {
            return await UpdateStatusAsync(id, userId, organizationId, etag, ActionItemStatus.Rejected, cancellationToken);
        }

        public async Task<Result> DeleteAsync(
            Guid id, Guid userId, Guid organizationId, string etag,
            CancellationToken cancellationToken = default)
        {
            var item = await _dbContext.ActionItems
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            if (item == null)
                return Result.Failure(new Error("NotFound", "Action item not found.", 404));

            if (!await CanModifyAsync(item.MeetingId, userId, organizationId, cancellationToken))
                return Result.Failure(new Error("Forbidden", "You do not have permission to delete this action item.", 403));

            var etagValidation = ValidateEtag(item, etag);
            if (etagValidation.IsFailure)
                return Result.Failure(etagValidation.Error);

            if (item.Status == ActionItemStatus.Synced || item.Status == ActionItemStatus.SyncedNoAssignee)
                return Result.Failure(new Error("Conflict", "Synced action items cannot be deleted.", 409));

            _dbContext.ActionItems.Remove(item);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        public async Task<Result<SyncResultResponse>> SyncToProviderAsync(
            Guid id, Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var item = await _dbContext.ActionItems
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            if (item == null)
                return Result.Failure<SyncResultResponse>(new Error("NotFound", "Action item not found.", 404));

            if (!await CanModifyAsync(item.MeetingId, userId, organizationId, cancellationToken))
                return Result.Failure<SyncResultResponse>(new Error("Forbidden", "You do not have permission to sync this action item.", 403));

            if (item.Status != ActionItemStatus.Approved)
                return Result.Failure<SyncResultResponse>(new Error("Conflict", "Only approved action items can be synced.", 409));

            var integration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId, cancellationToken);

            if (integration == null || integration.Status != IntegrationStatus.Active)
                return Result.Failure<SyncResultResponse>(new Error("BadRequest", "Integration is not configured or active.", 400));

            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == integration.Type, cancellationToken);

            if (config == null)
                return Result.Failure<SyncResultResponse>(new Error("BadRequest", "Integration configuration not found.", 400));

            var provider = _providerFactory.GetProvider(integration.Type.ToString());

            string? assigneeExternalId = null;
            string? missingReason = null;

            if (item.AssignedToUserId.HasValue)
            {
                var accountLink = await _dbContext.ExternalAccountLinks
                    .FirstOrDefaultAsync(l => l.UserId == item.AssignedToUserId.Value && l.OrganizationId == organizationId && l.Provider == integration.Type, cancellationToken);

                if (accountLink != null)
                {
                    assigneeExternalId = accountLink.ExternalUserId;
                }
                else
                {
                    var mapping = await _dbContext.ExternalMemberMappings
                        .FirstOrDefaultAsync(m => m.UserId == item.AssignedToUserId.Value && m.OrganizationId == organizationId && m.Provider == integration.Type, cancellationToken);

                    if (mapping != null)
                        assigneeExternalId = mapping.ExternalMemberId;
                }

                if (assigneeExternalId != null)
                {
                    var isValidAssignee = await provider.ValidateAssigneeAsync(config, config.SelectedProjectId, assigneeExternalId, cancellationToken);
                    if (!isValidAssignee)
                    {
                        assigneeExternalId = null;
                        missingReason = "NotProjectMember";
                    }
                }
                else
                {
                    missingReason = "UserNotConnected";
                }
            }

            try
            {
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
                item.SyncMissingAssigneeReason = missingReason;
                item.SyncedAtUtc = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync(cancellationToken);

                return Result.Success(new SyncResultResponse
                {
                    ActionItemId = item.Id,
                    Status = item.Status.ToString(),
                    ExternalTaskId = item.ExternalTaskId,
                    ExternalTaskUrl = item.ExternalTaskUrl,
                    SyncMissingAssigneeReason = item.SyncMissingAssigneeReason
                });
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                integration.Status = IntegrationStatus.NeedsReconnect;
                await _dbContext.SaveChangesAsync(cancellationToken);
                return Result.Failure<SyncResultResponse>(new Error("Unauthorized", "Integration credentials are invalid. Please reconnect.", 401));
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                integration.Status = IntegrationStatus.InvalidConfig;
                await _dbContext.SaveChangesAsync(cancellationToken);
                return Result.Failure<SyncResultResponse>(new Error("NotFound", "Project or list not found. Please reconfigure.", 404));
            }
        }

        public async Task<Result<BulkSyncResponse>> BulkSyncAsync(
            Guid meetingId, List<Guid> actionItemIds,
            Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            if (!await CanModifyAsync(meetingId, userId, organizationId, cancellationToken))
                return Result.Failure<BulkSyncResponse>(new Error("Forbidden", "You do not have permission to sync action items for this meeting.", 403));

            var integration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId, cancellationToken);

            if (integration == null)
                return Result.Failure<BulkSyncResponse>(new Error("BadRequest", "Integration not configured.", 400));

            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == integration.Type, cancellationToken);

            if (config == null)
                return Result.Failure<BulkSyncResponse>(new Error("BadRequest", "Integration configuration not found.", 400));

            var items = await _dbContext.ActionItems
                .Where(x => actionItemIds.Contains(x.Id) && x.MeetingId == meetingId && x.Status == ActionItemStatus.Approved)
                .ToListAsync(cancellationToken);

            var results = new List<BulkSyncItemResult>();

            foreach (var item in items)
            {
                try
                {
                    var singleResult = await SyncToProviderAsync(item.Id, userId, organizationId, cancellationToken);
                    if (singleResult.IsSuccess)
                    {
                        results.Add(new BulkSyncItemResult
                        {
                            ActionItemId = item.Id,
                            Status = singleResult.Value.Status,
                            ExternalTaskId = singleResult.Value.ExternalTaskId,
                            ExternalTaskUrl = singleResult.Value.ExternalTaskUrl,
                            SyncMissingAssigneeReason = singleResult.Value.SyncMissingAssigneeReason
                        });
                    }
                    else
                    {
                        results.Add(new BulkSyncItemResult
                        {
                            ActionItemId = item.Id,
                            Status = "Failed",
                            ErrorMessage = singleResult.Error.Description
                        });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new BulkSyncItemResult
                    {
                        ActionItemId = item.Id,
                        Status = "Failed",
                        ErrorMessage = ex.Message
                    });
                }
            }

            return Result.Success(new BulkSyncResponse
            {
                IntegrationStatus = integration.Status.ToString(),
                Results = results
            });
        }

        public async Task<Result> ReExtractAsync(
            Guid meetingId, Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            if (!await CanModifyAsync(meetingId, userId, organizationId, cancellationToken))
                return Result.Failure(new Error("Forbidden", "You do not have permission to re-extract action items.", 403));

            var existingCount = await _dbContext.ActionItems
                .CountAsync(x => x.MeetingId == meetingId, cancellationToken);

            if (existingCount > 0)
                return Result.Failure(new Error("Conflict", "Delete existing action items first before re-extracting.", 409));

            _backgroundJobClient.Enqueue<ExtractActionItemsJob>(
                job => job.RunAsync(meetingId, organizationId, CancellationToken.None));

            return Result.Success();
        }

        private async Task<bool> CanModifyAsync(Guid meetingId, Guid userId, Guid organizationId, CancellationToken cancellationToken)
        {
            var participant = await _dbContext.MeetingParticipants
                .FirstOrDefaultAsync(p => p.MeetingId == meetingId && p.UserId == userId, cancellationToken);

            if (participant != null && (participant.MeetingRole == MeetingRole.Host ||
                   participant.MeetingRole == MeetingRole.CoHost))
                return true;

            var membership = await _dbContext.UserOrgMemberships
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.IsEnabled, cancellationToken);

            return membership?.OrgRole == OrganizationRole.Admin;
        }

        private static Result ValidateEtag(ActionItem item, string etag)
        {
            if (string.IsNullOrEmpty(etag))
                return Result.Failure(new Error("PreconditionRequired", "If-Match header is required.", 428));

            var currentEtag = Convert.ToBase64String(item.RowVersion);
            if (!etag.Equals(currentEtag, StringComparison.Ordinal))
                return Result.Failure(new Error("Conflict", "The action item was modified by another user. Please refresh and try again.", 409));

            return Result.Success();
        }

        private async Task<Result<ActionItemResponse>> UpdateStatusAsync(
            Guid id, Guid userId, Guid organizationId, string etag, ActionItemStatus newStatus, CancellationToken cancellationToken)
        {
            var item = await _dbContext.ActionItems
                .Include(x => x.AssignedToUser)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            if (item == null)
                return Result.Failure<ActionItemResponse>(new Error("NotFound", "Action item not found.", 404));

            if (!await CanModifyAsync(item.MeetingId, userId, organizationId, cancellationToken))
                return Result.Failure<ActionItemResponse>(new Error("Forbidden", "You do not have permission to modify this action item.", 403));

            var etagValidation = ValidateEtag(item, etag);
            if (etagValidation.IsFailure)
                return Result.Failure<ActionItemResponse>(etagValidation.Error);

            if (item.Status == ActionItemStatus.Synced || item.Status == ActionItemStatus.SyncedNoAssignee)
                return Result.Failure<ActionItemResponse>(new Error("Conflict", "Action item has already been synced and is immutable.", 409));

            if (!IsValidTransition(item.Status, newStatus))
                return Result.Failure<ActionItemResponse>(new Error("Conflict", $"Invalid status transition from {item.Status} to {newStatus}.", 409));

            item.Status = newStatus;

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<ActionItemResponse>(new Error("Conflict", "The action item was modified by another user. Please refresh and try again.", 409));
            }

            return Result.Success(MapToResponse(item));
        }

        private static bool IsValidTransition(ActionItemStatus from, ActionItemStatus to)
        {
            if (from == ActionItemStatus.Synced || from == ActionItemStatus.SyncedNoAssignee)
                return false;

            return true;
        }

        private static ActionItemResponse MapToResponse(ActionItem item)
        {
            return new ActionItemResponse
            {
                Id = item.Id,
                MeetingId = item.MeetingId,
                Title = item.Title,
                Description = item.Description,
                AssignedToUserId = item.AssignedToUserId,
                AssignedToUserName = item.AssignedToUser?.UserName,
                DueDateUtc = item.DueDateUtc,
                Status = item.Status.ToString(),
                ExternalTaskId = item.ExternalTaskId,
                ExternalTaskUrl = item.ExternalTaskUrl,
                ExternalProvider = item.ExternalProvider.ToString(),
                SyncMissingAssigneeReason = item.SyncMissingAssigneeReason,
                ExtractedAtUtc = item.ExtractedAtUtc,
                SyncedAtUtc = item.SyncedAtUtc
            };
        }
    }
}
