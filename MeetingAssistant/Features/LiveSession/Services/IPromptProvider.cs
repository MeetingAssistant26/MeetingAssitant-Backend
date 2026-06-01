namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IPromptProvider
    {
        string GetSummarizerPrompt(string transcript);
        string GetTaskExtractionPrompt(string transcript);
    }

    public class PromptProvider : IPromptProvider
    {
        private readonly string _summarizerPrompt;
        private readonly string _taskExtractionPrompt;

        public PromptProvider(IHostEnvironment hostEnvironment)
        {
            var promptDirectory = Path.Combine(
                hostEnvironment.ContentRootPath,
                "Features",
                "LiveSession",
                "Resources",
                "Prompts");

            _summarizerPrompt = ReadPrompt(promptDirectory, "MeetingSummarizer.md");
            _taskExtractionPrompt = ReadPrompt(promptDirectory, "TaskExtraction.md");
        }

        public string GetSummarizerPrompt(string transcript) => FillTranscriptPlaceholder(_summarizerPrompt, transcript);

        public string GetTaskExtractionPrompt(string transcript) => FillTranscriptPlaceholder(_taskExtractionPrompt, transcript);

        private static string ReadPrompt(string promptDirectory, string fileName)
        {
            var promptPath = Path.Combine(promptDirectory, fileName);
            var prompt = File.ReadAllText(promptPath);

            if (!prompt.Contains("{transcript}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Prompt '{fileName}' must contain the '{{transcript}}' placeholder.");
            }

            return prompt;
        }

        private static string FillTranscriptPlaceholder(string promptTemplate, string transcript)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
            return promptTemplate.Replace("{transcript}", transcript, StringComparison.Ordinal);
        }
    }
}
