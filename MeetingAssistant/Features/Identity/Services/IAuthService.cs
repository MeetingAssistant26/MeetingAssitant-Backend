using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Models.Requests;
using MeetingAssistant.Features.Identity.Models.Responses;
using MeetingAssistant.Features.Identity.DTOs;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Identity.Services
{
    public interface IAuthService
    {
        Task<Result<AuthTokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
        Task<Result<AuthTokenResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken);
        Task<Result> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken);
        Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
        Task<Result> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken);
        Task<Result> ResendConfirmationEmailAsync(ResendConfirmationEmailRequest request, CancellationToken cancellationToken);
        Task<Result> SendResetPasswordCodeAsync(ForgetPasswordRequest request, CancellationToken cancellationToken);
        Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
    }
}
