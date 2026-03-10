namespace MeetingAssistant.Features.Identity.DTOs;

public record ResetPasswordRequest(
    string Email,
    string Code,
    string NewPassword
);