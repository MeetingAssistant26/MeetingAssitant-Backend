namespace MeetingAssistant.Features.LiveSession.Models
{
    public static class AiAssistantTraceEventTypes
    {
        public const string AssistantSpeechCompleted = "assistant_speech_completed";
        public const string LlmCompleted = "llm_completed";
        public const string SttCompleted = "stt_completed";
        public const string TtsCompleted = "tts_completed";
        public const string TurnStarted = "turn_started";
    }
}
