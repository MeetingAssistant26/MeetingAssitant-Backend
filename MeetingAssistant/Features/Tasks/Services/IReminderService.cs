using MeetingAssistant.Features.Tasks.Contracts.Requests;
using MeetingAssistant.Features.Tasks.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Tasks.Services
{
    public interface IReminderService
    {
        Task<Result<ReminderResponse>> CreateReminderAsync(
            CreateMyReminderRequest request,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result<ReminderListResponse>> GetMyRemindersAsync(
            Guid userId,
            Guid organizationId,
            int page,
            int pageSize,
            bool includeFuture = false,
            CancellationToken cancellationToken = default);

        Task<Result<ReminderResponse>> UpdateReminderAsync(
            Guid reminderId,
            UpdateMyReminderRequest request,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result<ReminderResponse>> MarkDeliveredAsync(
            Guid reminderId,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result> CancelReminderAsync(
            Guid reminderId,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default);
    }
}
