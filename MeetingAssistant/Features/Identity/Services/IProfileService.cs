using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Identity.Services
{
    public interface IProfileService
    {
        Task<Result> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
        Task<Result> UpdateProfileAsync(Guid userId, object request, CancellationToken cancellationToken);
        Task<Result> ChangePasswordAsync(Guid userId, object request, CancellationToken cancellationToken);
    }
}
