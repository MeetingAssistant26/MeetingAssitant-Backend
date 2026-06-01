using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace MeetingAssistant.Shared.Errors
{
    public static class AiDebugErrors
    {
        public static readonly Error Disabled = new(
            "AiDebug.Disabled",
            "AI assistant debug tracing is disabled.",
            StatusCodes.Status404NotFound);

        public static readonly Error InvalidTrace = new(
            "AiDebug.InvalidTrace",
            "Trace event payload is invalid.",
            StatusCodes.Status400BadRequest);
    }
}
