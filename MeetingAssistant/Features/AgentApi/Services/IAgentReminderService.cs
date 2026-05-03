using MeetingAssistant.Features.AgentApi.Models.Requests;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.AgentApi.Services
{
    public interface IAgentReminderService
    {
        Task<Result<AgentReminderResponse>> CreateReminderAsync(
            CreateAgentReminderRequest request,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);

        Task<Result<IReadOnlyList<AgentReminderResponse>>> ListPublicMeetingRemindersAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);

        Task<Result> MarkReminderDeliveredAsync(
            Guid reminderId,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);

        Task<Result> CancelReminderAsync(
            Guid reminderId,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);
    }
}
