using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
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
        private const string NeedsAssigneeReviewReason = "NeedsAssignee";
        private const int MaxTitleLength = 200;
        private const int MaxDescriptionLength = 2000;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ApplicationDbContext _dbContext;
        private readonly ILLMService _llmService;
        private readonly IPromptProvider _promptProvider;
        private readonly ILogger<ExtractActionItemsJob> _logger;
        private readonly string _model;

        public ExtractActionItemsJob(
            ApplicationDbContext dbContext,
            ILLMService llmService,
            IPromptProvider promptProvider,
            IConfiguration configuration,
            ILogger<ExtractActionItemsJob> logger)
        {
            _dbContext = dbContext;
            _llmService = llmService;
            _promptProvider = promptProvider;
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
            var participantCandidates = participants
                .Select(p => new ParticipantCandidate(p.Id, p.UserId, p.DisplayName, p.UserName))
                .ToList();

            var prompt = _promptProvider.GetTaskExtractionPrompt(transcript.FullText);

            try
            {
                var llmRequest = new LLMRequest
                {
                    Model = _model,
                    Messages = new List<ChatMessage>
                    {
                        new() { Role = "user", Content = prompt }
                    },
                    ResponseFormat = new ResponseFormat { Type = "json_object" }
                };

                var response = await _llmService.CompleteAsync(llmRequest, cancellationToken);
                var result = ParseResponse(response, meetingId);

                if (result == null || result.Tasks.Count == 0)
                {
                    _logger.LogInformation("No action items extracted for meeting {MeetingId}", meetingId);
                    return;
                }

                var createdCount = 0;
                foreach (var item in result.Tasks)
                {
                    if (string.IsNullOrWhiteSpace(item.Task))
                    {
                        _logger.LogWarning("Skipping extracted action item with empty task for meeting {MeetingId}", meetingId);
                        continue;
                    }

                    var assignee = ResolveAssignee(item.Assignee, participantCandidates);
                    var dueDateUtc = ParseDueDateUtc(item.DueDate, out var invalidDueDate);

                    var actionItem = new ActionItem
                    {
                        OrganizationId = organizationId,
                        MeetingId = meetingId,
                        Title = Truncate(item.Task.Trim(), MaxTitleLength),
                        Description = BuildDescription(item, assignee, invalidDueDate),
                        AssignedToParticipantId = assignee.ParticipantId,
                        AssignedToUserId = assignee.UserId,
                        DueDateUtc = dueDateUtc,
                        Status = ActionItemStatus.PendingReview,
                        SyncMissingAssigneeReason = assignee.RequiresReview ? NeedsAssigneeReviewReason : null,
                        ExtractedAtUtc = DateTime.UtcNow
                    };

                    _dbContext.ActionItems.Add(actionItem);
                    createdCount++;
                }

                if (createdCount == 0)
                {
                    _logger.LogInformation("No usable action items extracted for meeting {MeetingId}", meetingId);
                    return;
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Extracted {Count} action items for meeting {MeetingId}", createdCount, meetingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract action items for meeting {MeetingId}", meetingId);
                throw;
            }
        }

        private ExtractedTasksDto? ParseResponse(LLMResponse response, Guid meetingId)
        {
            var content = response.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("LLM returned no action item content for meeting {MeetingId}", meetingId);
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<ExtractedTasksDto>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "LLM returned malformed action item JSON for meeting {MeetingId}", meetingId);
                return null;
            }
        }

        private static AssigneeResolution ResolveAssignee(
            string? rawAssignee,
            IReadOnlyList<ParticipantCandidate> participants)
        {
            var cleanedAssignee = NormalizeSuggestedAssignee(rawAssignee);
            if (string.IsNullOrWhiteSpace(cleanedAssignee))
            {
                return AssigneeResolution.NeedsReview(rawAssignee);
            }

            var matches = participants
                .Where(p => p.Matches(cleanedAssignee))
                .ToList();

            return matches.Count == 1
                ? AssigneeResolution.Matched(rawAssignee, matches[0].ParticipantId, matches[0].UserId)
                : AssigneeResolution.NeedsReview(rawAssignee);
        }

        private static string? NormalizeSuggestedAssignee(string? assignee)
        {
            if (string.IsNullOrWhiteSpace(assignee))
            {
                return null;
            }

            return assignee
                .Replace("(suggested)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private static DateTime? ParseDueDateUtc(string? dueDate, out bool invalidDueDate)
        {
            invalidDueDate = false;

            if (string.IsNullOrWhiteSpace(dueDate))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(
                    dueDate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dateTimeOffset))
            {
                return dateTimeOffset.UtcDateTime;
            }

            invalidDueDate = true;
            return null;
        }

        private static string BuildDescription(
            ExtractedTaskDto item,
            AssigneeResolution assignee,
            bool invalidDueDate)
        {
            var lines = new List<string> { item.Task.Trim() };

            if (!string.IsNullOrWhiteSpace(assignee.RawAssignee))
            {
                lines.Add($"AI assignee: {assignee.RawAssignee}");
            }

            if (!string.IsNullOrWhiteSpace(item.DueDate))
            {
                var suffix = invalidDueDate ? " (could not parse to UTC)" : string.Empty;
                lines.Add($"AI due date: {item.DueDate}{suffix}");
            }

            return Truncate(string.Join(Environment.NewLine, lines), MaxDescriptionLength);
        }

        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private sealed class ExtractedTasksDto
        {
            [JsonPropertyName("tasks")]
            public List<ExtractedTaskDto> Tasks { get; set; } = new();
        }

        private sealed class ExtractedTaskDto
        {
            [JsonPropertyName("assignee")]
            public string? Assignee { get; set; }

            [JsonPropertyName("task")]
            public string Task { get; set; } = string.Empty;

            [JsonPropertyName("due_date")]
            public string? DueDate { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }
        }

        private sealed record ParticipantCandidate(
            Guid ParticipantId,
            Guid UserId,
            string? DisplayName,
            string? UserName)
        {
            public bool Matches(string assignee)
            {
                var normalizedAssignee = NormalizeForMatch(assignee);
                var candidates = new[]
                    {
                        DisplayName,
                        UserName,
                        UserName?.Split('@', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    }
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Select(candidate => NormalizeForMatch(candidate!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (candidates.Any(candidate => string.Equals(candidate, normalizedAssignee, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return normalizedAssignee.Length >= 3
                    && candidates.Any(candidate => candidate.Contains(normalizedAssignee, StringComparison.OrdinalIgnoreCase));
            }

            private static string NormalizeForMatch(string value)
                => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private sealed record AssigneeResolution(
            string? RawAssignee,
            Guid? ParticipantId,
            Guid? UserId,
            bool RequiresReview)
        {
            public static AssigneeResolution Matched(string? rawAssignee, Guid participantId, Guid userId)
                => new(rawAssignee, participantId, userId, false);

            public static AssigneeResolution NeedsReview(string? rawAssignee)
                => new(rawAssignee, null, null, true);
        }
    }
}
