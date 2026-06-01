
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Services
{
    public interface IParticipantService
    {
        Task<Result<ParticipantResponse>> AddParticipantAsync(
            Guid meetingId,
            AddParticipantRequest request,
            Guid callerId,
            CancellationToken cancellationToken = default);

        Task<Result> RemoveParticipantAsync(
            Guid meetingId,
            Guid userIdToRemove,
            Guid callerId,
            CancellationToken cancellationToken = default);

        Task<Result<ParticipantResponse>> UpdateParticipantRoleAsync(
            Guid meetingId,
            Guid userIdToUpdate,
            UpdateParticipantRoleRequest request,
            Guid callerId,
            CancellationToken cancellationToken = default);

        Task<Result<List<ConflictResponse>>> CheckConflictsAsync(
            Guid meetingId,
            Guid callerId,
            CancellationToken cancellationToken = default);
    }
}
