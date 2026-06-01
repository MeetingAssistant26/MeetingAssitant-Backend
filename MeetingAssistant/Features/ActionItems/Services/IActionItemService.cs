using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Services
{
    public interface IActionItemService
    {
        Task<Result<ActionItemListResponse>> GetByMeetingAsync(
            Guid meetingId, Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result<ActionItemListResponse>> ListOrganizationActionItemsAsync(
            Guid organizationId,
            Guid userId,
            int page,
            int pageSize,
            string? assignee,
            Guid? meetingId,
            string? status,
            string? provider,
            DateTime? fromUtc,
            DateTime? toUtc,
            CancellationToken cancellationToken = default);

        Task<Result<ActionItemResponse>> UpdateAsync(
            Guid id, UpdateActionItemRequest request,
            Guid userId, Guid organizationId, string? etag,
            CancellationToken cancellationToken = default);

        Task<Result<ActionItemResponse>> ApproveAsync(
            Guid id, Guid userId, Guid organizationId, string? etag,
            CancellationToken cancellationToken = default);

        Task<Result<ActionItemResponse>> RejectAsync(
            Guid id, Guid userId, Guid organizationId, string? etag,
            CancellationToken cancellationToken = default);

        Task<Result> DeleteAsync(
            Guid id, Guid userId, Guid organizationId, string? etag,
            CancellationToken cancellationToken = default);

        Task<Result<SyncResultResponse>> SyncToProviderAsync(
            Guid id, Guid userId, Guid organizationId, string? etag,
            CancellationToken cancellationToken = default);

        Task<Result<BulkSyncResponse>> BulkSyncAsync(
            Guid meetingId, List<Guid> actionItemIds,
            Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result> ReExtractAsync(
            Guid meetingId, Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default);
    }
}
