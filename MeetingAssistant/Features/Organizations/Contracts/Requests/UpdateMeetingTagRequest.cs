using System.Text.Json.Serialization;
using MeetingAssistant.Shared.Types;

namespace MeetingAssistant.Features.Organizations.Contracts.Requests
{
    public record UpdateMeetingTagRequest(string? Name, Optional<string?> Color);
}
