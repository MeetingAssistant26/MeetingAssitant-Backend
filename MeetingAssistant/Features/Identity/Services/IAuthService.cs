using MeetingAssistant.Features.Identity.DTOs;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Identity.Services
{
    public interface IAuthService
    {
        Task<Result<AuthResponse>> GetTokenAsync( string email, string password, CancellationToken cancellationToken);
        Task<Result<AuthResponse>> GetRefreshTokenAsync(string token, string refreshToken, CancellationToken cancellationToken);
        Task<Result> RevokeRefreshTokenAsync(string token,string refreshToken, CancellationToken cancellationToken);
        Task<Result> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
        Task<Result> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken);
        Task<Result> ResendConfirmationEmailAsync(ResendConfirmationEmailRequest request, CancellationToken cancellationToken);
        Task<Result> SendResetPasswordCodeAsync(ForgetPasswordRequest request, CancellationToken cancellationToken);
        Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
    }
}
