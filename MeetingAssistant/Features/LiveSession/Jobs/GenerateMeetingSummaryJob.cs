using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class GenerateMeetingSummaryJob(
        ApplicationDbContext dbContext,
        ISummarizerService summarizerService,
        ILogger<GenerateMeetingSummaryJob> logger)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISummarizerService _summarizerService = summarizerService;
        private readonly ILogger<GenerateMeetingSummaryJob> _logger = logger;

        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            if (transcript == null || string.IsNullOrWhiteSpace(transcript.FullText))
            {
                _logger.LogInformation(
                    "Summary generation skipped because meeting transcript was unavailable. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            var summaryResult = await _summarizerService.SummarizeAsync(transcript.FullText, cancellationToken);

            var summary = await _dbContext.MeetingSummaries
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            if (summary == null)
            {
                summary = new MeetingSummary
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId
                };

                _dbContext.MeetingSummaries.Add(summary);
            }

            summary.SummaryText = summaryResult.SummaryText;
            summary.LlmModel = summaryResult.Model;
            summary.PromptTokens = summaryResult.PromptTokens;
            summary.CompletionTokens = summaryResult.CompletionTokens;
            summary.GeneratedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
