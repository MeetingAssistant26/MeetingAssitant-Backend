using System.Security.Cryptography;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IPromptProvider
    {
        string SummarizerPromptName { get; }
        string SummarizerPromptVersion { get; }
        string PersonalizedSummarizerPromptName { get; }
        string PersonalizedSummarizerPromptVersion { get; }

        string GetSummarizerPrompt(string transcript);
        string GetTaskExtractionPrompt(string transcript);
        string GetPersonalizedSummarizerPrompt(
            string participant,
            string transcript,
            string? personalizationContext = null);
    }

    public class PromptProvider : IPromptProvider
    {
        public const string MeetingSummarizerPromptName = "MeetingSummarizer";
        public const string PersonalizedMeetingSummarizerPromptName = "PersonalizedMeetingSummarizer";

        private readonly string _summarizerPrompt;
        private readonly string _taskExtractionPrompt;
        private readonly string _personalizedSummarizerPrompt;
        private readonly string _summarizerPromptVersion;
        private readonly string _personalizedSummarizerPromptVersion;

        public PromptProvider(IHostEnvironment hostEnvironment)
        {
            var promptDirectory = Path.Combine(
                hostEnvironment.ContentRootPath,
                "Features",
                "LiveSession",
                "Resources",
                "Prompts");

            _summarizerPrompt = ReadPrompt(promptDirectory, "MeetingSummarizer.md", ["{transcript}"]);
            _taskExtractionPrompt = ReadPrompt(promptDirectory, "TaskExtraction.md", ["{transcript}"]);
            _personalizedSummarizerPrompt = ReadPrompt(
                promptDirectory,
                "PersonalizedMeetingSummarizer.md",
                ["{participant}", "{personalization_context}", "{transcript}"]);
            _summarizerPromptVersion = ComputePromptVersion(_summarizerPrompt);
            _personalizedSummarizerPromptVersion = ComputePromptVersion(_personalizedSummarizerPrompt);
        }

        public string SummarizerPromptName => MeetingSummarizerPromptName;

        public string SummarizerPromptVersion => _summarizerPromptVersion;

        public string PersonalizedSummarizerPromptName => PersonalizedMeetingSummarizerPromptName;

        public string PersonalizedSummarizerPromptVersion => _personalizedSummarizerPromptVersion;

        public string GetSummarizerPrompt(string transcript) => FillTranscriptPlaceholder(_summarizerPrompt, transcript);

        public string GetTaskExtractionPrompt(string transcript) => FillTranscriptPlaceholder(_taskExtractionPrompt, transcript);

        public string GetPersonalizedSummarizerPrompt(
            string participant,
            string transcript,
            string? personalizationContext = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(participant);
            ArgumentException.ThrowIfNullOrWhiteSpace(transcript);

            return _personalizedSummarizerPrompt
                .Replace("{participant}", participant.Trim(), StringComparison.Ordinal)
                .Replace("{personalization_context}", personalizationContext?.Trim() ?? string.Empty, StringComparison.Ordinal)
                .Replace("{transcript}", transcript, StringComparison.Ordinal);
        }

        private static string ReadPrompt(string promptDirectory, string fileName, IReadOnlyCollection<string> requiredPlaceholders)
        {
            var promptPath = Path.Combine(promptDirectory, fileName);
            var prompt = File.ReadAllText(promptPath);

            foreach (var placeholder in requiredPlaceholders)
            {
                if (!prompt.Contains(placeholder, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Prompt '{fileName}' must contain the '{placeholder}' placeholder.");
                }
            }

            return prompt;
        }

        private static string FillTranscriptPlaceholder(string promptTemplate, string transcript)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
            return promptTemplate.Replace("{transcript}", transcript, StringComparison.Ordinal);
        }

        private static string ComputePromptVersion(string promptTemplate)
        {
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(promptTemplate));
            return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
        }
    }
}
