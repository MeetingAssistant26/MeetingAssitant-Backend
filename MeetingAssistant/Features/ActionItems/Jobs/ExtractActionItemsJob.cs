using System.Text.Json;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Features.ActionItems.Jobs
{
    public class ExtractActionItemsJob
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILLMService _llmService;
        private readonly ILogger<ExtractActionItemsJob> _logger;
        private readonly string _model;

        public ExtractActionItemsJob(
            ApplicationDbContext dbContext,
            ILLMService llmService,
            IConfiguration configuration,
            ILogger<ExtractActionItemsJob> logger)
        {
            _dbContext = dbContext;
            _llmService = llmService;
            _logger = logger;
            _model = configuration["AI:Model"] ?? "gpt-4o-mini";
        }

        [Hangfire.AutomaticRetry(Attempts = 3)]
        public async Task RunAsync(Guid meetingId, Guid organizationId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting action item extraction for meeting {MeetingId}", meetingId);

            var existingCount = await _dbContext.ActionItems
                .CountAsync(x => x.MeetingId == meetingId, cancellationToken);

            if (existingCount > 0)
            {
                _logger.LogInformation("Action items already exist for meeting {MeetingId}. Skipping.", meetingId);
                return;
            }

            var transcript = await _dbContext.MeetingTranscripts
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.MeetingId == meetingId, cancellationToken);

            if (transcript == null || string.IsNullOrWhiteSpace(transcript.FullText))
            {
                _logger.LogWarning("No transcript found for meeting {MeetingId}", meetingId);
                return;
            }

            var participants = await _dbContext.MeetingParticipants
                .AsNoTracking()
                .Where(p => p.MeetingId == meetingId)
                .Select(p => new { p.Id, p.UserId, p.User.UserName, p.User.DisplayName })
                .ToListAsync(cancellationToken);

            var rosterJson = JsonSerializer.Serialize(participants.Select(p => new
            {
                participantId = p.Id,
                userId = p.UserId,
                name = p.DisplayName ?? p.UserName
            }));

            var prompt = $$"""
                Extract action items from the following meeting transcript.
                
                Participant roster (use ONLY these participant IDs for assignees):
                {{rosterJson}}
                
                Transcript:
                {{transcript.FullText}}
                
                Return a JSON object with this exact structure:
                {
                    "actionItems": [
                        {
                            "title": "Action item title",
                            "description": "Detailed description",
                            "assignedParticipantId": "guid or null",
                            "dueDate": "ISO 8601 date or null"
                        }
                    ]
                }
                """;

            try
            {
                var llmRequest = new LLMRequest
                {
                    Model = _model,
                    Messages = new List<ChatMessage>
                    {
                        new() { Role = "system", Content = "You are an action item extractor. Return only valid JSON." },
                        new() { Role = "user", Content = prompt }
                    }
                };

                var result = await _llmService.CompleteWithJsonAsync<ExtractedActionItemsDto>(llmRequest, cancellationToken);

                if (result?.ActionItems == null || result.ActionItems.Count == 0)
                {
                    _logger.LogInformation("No action items extracted for meeting {MeetingId}", meetingId);
                    return;
                }

                foreach (var item in result.ActionItems)
                {
                    Guid? assignedParticipantId = null;
                    Guid? assignedUserId = null;

                    if (!string.IsNullOrEmpty(item.AssignedParticipantId) && Guid.TryParse(item.AssignedParticipantId, out var parsedId))
                    {
                        assignedParticipantId = parsedId;
                        var participant = participants.FirstOrDefault(p => p.Id == parsedId);
                        assignedUserId = participant?.UserId;
                    }

                    var actionItem = new ActionItem
                    {
                        OrganizationId = organizationId,
                        MeetingId = meetingId,
                        Title = item.Title ?? "Untitled Action Item",
                        Description = item.Description,
                        AssignedToParticipantId = assignedParticipantId,
                        AssignedToUserId = assignedUserId,
                        DueDateUtc = item.DueDate,
                        Status = ActionItemStatus.PendingReview,
                        ExtractedAtUtc = DateTime.UtcNow
                    };

                    _dbContext.ActionItems.Add(actionItem);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Extracted {Count} action items for meeting {MeetingId}", result.ActionItems.Count, meetingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract action items for meeting {MeetingId}", meetingId);
                throw;
            }
        }

        private class ExtractedActionItemsDto
        {
            public List<ExtractedActionItemDto> ActionItems { get; set; } = new();
        }

        private class ExtractedActionItemDto
        {
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string? AssignedParticipantId { get; set; }
            public DateTime? DueDate { get; set; }
        }
    }
}
