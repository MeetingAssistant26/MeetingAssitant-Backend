using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Responses;
using MeetingAssistant.Features.Identity.Models.Requests;

namespace MeetingAssistant.Features.Identity.Services
{
    public interface IProfileService
    {
        Task<Result<UserProfileResponse>> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
        Task<Result<UserProfileResponse>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken cancellationToken);
        Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken);
    }
}
