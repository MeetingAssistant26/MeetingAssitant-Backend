using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Services
{
    public interface IRecurrenceService
    {
        Task<Result<RecurringMeetingCreationResponse>> GenerateMeetingInstancesAsync(
            Guid userId,
            CreateRecurringMeetingRequest request,
            CancellationToken cancellationToken = default);
    }
}