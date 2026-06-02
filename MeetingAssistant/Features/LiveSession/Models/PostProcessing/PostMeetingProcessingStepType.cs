namespace MeetingAssistant.Features.LiveSession.Models.PostProcessing
{
    public enum PostMeetingProcessingStepType
    {
        RoomCompleted = 0,
        FragmentDiscovery = 1,
        AudioIngest = 2,
        Stt = 3,
        TranscriptPersistence = 4,
        SummaryGeneration = 5,
        ActionExtraction = 6,
        TagSuggestion = 7,
        KnowledgeIndexing = 8,
        ProviderSync = 9,
        PersonalizedSummaryGeneration = 10
    }
}
