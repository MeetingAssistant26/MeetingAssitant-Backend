namespace MeetingAssistant.Features.Identity.Models.Requests
{
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}