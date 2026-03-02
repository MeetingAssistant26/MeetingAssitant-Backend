namespace MeetingAssistant.Contracts.Authentication
{
    public record ConfirmEmailRequest
    (
        string UserId,
        string Code
    );
    
}
