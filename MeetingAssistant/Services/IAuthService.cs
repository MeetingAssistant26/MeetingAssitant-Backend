
using MeetingAssistant.Abstractions;
using MeetingAssistant.Contracts.Authentication;
using MeetingAssistant.Contracts.Authentication;

namespace MeetingAssistant.Services
{
    public interface IAuthService
    {
        Task<Result<AuthResponse>> GetTokenAsync( string email, string password, CancellationToken cancellationToken);
        Task<Result<AuthResponse>> GetRefreshTokenAsync(string token, string refreshToken, CancellationToken cancellationToken);
        Task<Result> RevokeRefreshTokenAsync(string token,string refreshToken, CancellationToken cancellationToken);
        Task<Result> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
        Task<Result> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken);
        Task<Result> ResendConfirmationEmailAsync(ResendConfirmationEmailRequest request, CancellationToken cancellationToken);
        Task<Result> SendResetPasswordCodeAsync(ForgetPasswordRequest request);
        Task<Result> ResetPasswordAsync(ResetPasswordRequest request);
    }
}
