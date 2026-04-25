namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IPromptProvider
    {
        string GetSummarizerPrompt();
    }

    public class PromptProvider : IPromptProvider
    {
        private readonly string _summarizerPrompt;

        public PromptProvider(IHostEnvironment hostEnvironment)
        {
            var promptPath = Path.Combine(
                hostEnvironment.ContentRootPath,
                "Features",
                "LiveSession",
                "Resources",
                "Prompts",
                "MeetingSummarizer.md");

            _summarizerPrompt = File.ReadAllText(promptPath);
        }

        public string GetSummarizerPrompt() => _summarizerPrompt;
    }
}
