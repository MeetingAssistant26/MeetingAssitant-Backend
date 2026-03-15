namespace MeetingAssistant.Features.Identity.DTOs
{
    public record ConfirmEmailRequest
    (
        string UserId,
        string Code
    );
    
}
