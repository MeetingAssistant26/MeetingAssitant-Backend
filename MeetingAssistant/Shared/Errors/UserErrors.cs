using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Shared.Errors
{
    public class UserErrors
    {
        public static readonly Error InvalidCredentials = new("User.InvalidCredentials", "Invalid email/password",StatusCodes.Status401Unauthorized);
        public static readonly Error InvalidToken = new("User.InvalidToken", "Invalid Token",StatusCodes.Status401Unauthorized);
        public static readonly Error UserNotFound = new("User.NotFound", "User Not Found",StatusCodes.Status404NotFound);
        public static readonly Error InvalidRefreshToken = new("User.InvalidRefreshToken", "RefreshToken Invalid/Not active",StatusCodes.Status401Unauthorized);
        public static readonly Error EmailNotConfirmed = new("User.EmailNotConfirmed", "EmailNotConfirmed", StatusCodes.Status401Unauthorized);
        public static readonly Error DuplicatedEmail = new("User.DuplicatedEmail", "Another User Already Using This Email Address", StatusCodes.Status409Conflict);
        public static readonly Error InvalidCode = new("User.InvalidCode", "InvalidCode", StatusCodes.Status401Unauthorized);
        public static readonly Error AccountLocked = new("User.AccountLocked", "Account is locked due to multiple failed login attempts. Please try again later.", StatusCodes.Status429TooManyRequests);
        public static readonly Error DuplicatedConfirmation = new("User.DuplicatedConfirmation", "Email is Already Confirmed", StatusCodes.Status409Conflict);

    }
}
