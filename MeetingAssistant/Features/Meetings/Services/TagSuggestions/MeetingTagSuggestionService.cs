using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.Meetings.Services.TagSuggestions
{
    public interface IMeetingTagSuggestionService
    {
        Task<MeetingTagSuggestionResult> SuggestTagsAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);
    }

    public sealed record MeetingTagSuggestionResult(
        int PersistedSuggestionCount,
        int IgnoredSuggestionCount,
        IReadOnlyList<Guid> SuggestionIds,
        IReadOnlyList<Guid> SuggestedTagIds,
        bool SkippedBecauseNoOrgTags = false);

    public sealed class MeetingTagSuggestionService(
        ApplicationDbContext dbContext,
        ILLMService llmService,
        IOptions<OpenAiCompatibleOptions> options,
        ILogger<MeetingTagSuggestionService> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null) : IMeetingTagSuggestionService
    {
        private const int MaxReasonLength = 1000;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ILLMService _llmService = llmService;
        private readonly OpenAiCompatibleOptions.ProviderConfig _llmOptions = options.Value.Llm;
        private readonly ILogger<MeetingTagSuggestionService> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        public async Task<MeetingTagSuggestionResult> SuggestTagsAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Id == meetingId)
                .Select(x => new { x.Id, x.OrganizationId, x.Title })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Meeting '{meetingId}' was not found for tag suggestion.");

            var orgTags = await _dbContext.MeetingTags
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.IsActive)
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            if (orgTags.Count == 0)
            {
                await SupersedePendingSuggestionsAsync(organizationId, meetingId, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.RecordEventAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingEventType.Info,
                        PostMeetingProcessingStepType.TagSuggestion,
                        PostMeetingProcessingStatus.Completed,
                        message: "Tag suggestion skipped because the organization has no active predefined meeting tags.",
                        metadataJson: JsonSerializer.Serialize(new { orgTagCount = 0 }, JsonOptions),
                        cancellationToken: cancellationToken);
                }

                return new MeetingTagSuggestionResult(0, 0, Array.Empty<Guid>(), Array.Empty<Guid>(), SkippedBecauseNoOrgTags: true);
            }

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (transcript is null || string.IsNullOrWhiteSpace(transcript.FullText))
            {
                throw new InvalidOperationException("Meeting transcript is required for tag suggestion.");
            }

            var summary = await _dbContext.MeetingSummaries
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var prompt = BuildPrompt(meeting.Title, transcript.FullText, summary?.SummaryText, orgTags);
            var request = new LLMRequest
            {
                Model = _llmOptions.Model,
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "user",
                        Content = prompt
                    }
                ],
                ResponseFormat = new ResponseFormat { Type = "json_object" },
                Temperature = 0.1,
                MaxTokens = 1200
            };

            var llmResponse = await _llmService.CompleteAsync(request, cancellationToken);
            var parsed = ParseResponse(llmResponse, meetingId);
            var validation = ValidateSuggestions(parsed.SuggestedTags, orgTags);

            var now = DateTime.UtcNow;
            var supersededExisting = await SupersedePendingSuggestionsAsync(organizationId, meetingId, cancellationToken);
            if (supersededExisting)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            var metadata = new SuggestionMetadata(
                SchemaVersion: 1,
                Source: "ai_work_llm",
                TranscriptId: transcript.Id,
                SummaryId: summary?.Id,
                OrgTagCount: orgTags.Count,
                RequestedTagIds: orgTags.Select(x => x.Id).ToArray(),
                RawSuggestedCount: parsed.SuggestedTags.Count,
                IgnoredSuggestions: validation.IgnoredSuggestions,
                KnowledgeRefreshPending: true,
                UsesOnlyConfirmedTagsForRag: true);

            var suggestions = validation.ValidSuggestions
                .Select(suggestion => new MeetingTagSuggestion
                {
                    OrganizationId = organizationId,
                    MeetingId = meetingId,
                    MeetingTagId = suggestion.Tag.Id,
                    TagNameSnapshot = suggestion.Tag.Name,
                    TagColorSnapshot = suggestion.Tag.Color,
                    Confidence = suggestion.Confidence,
                    Reason = Truncate(suggestion.Reason, MaxReasonLength),
                    Status = MeetingTagSuggestionStatus.PendingReview,
                    LlmModel = llmResponse.Model ?? _llmOptions.Model,
                    TranscriptId = transcript.Id,
                    SummaryId = summary?.Id,
                    SuggestedAtUtc = now,
                    MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions)
                })
                .ToList();

            _dbContext.MeetingTagSuggestions.AddRange(suggestions);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Persisted {PersistedCount} tag suggestion(s), ignored {IgnoredCount}. MeetingId={MeetingId} OrganizationId={OrganizationId}",
                suggestions.Count,
                validation.IgnoredSuggestions.Count,
                meetingId,
                organizationId);

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.RecordEventAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingEventType.Info,
                    PostMeetingProcessingStepType.TagSuggestion,
                    PostMeetingProcessingStatus.Completed,
                    message: $"Validated {suggestions.Count} tag suggestion(s); ignored {validation.IgnoredSuggestions.Count} invalid suggestion(s).",
                    artifact: new PostMeetingArtifactLink("meeting_tag_suggestion", ArtifactIds: suggestions.Select(x => x.Id).ToArray()),
                    metadataJson: JsonSerializer.Serialize(metadata, JsonOptions),
                    cancellationToken: cancellationToken);
            }

            return new MeetingTagSuggestionResult(
                suggestions.Count,
                validation.IgnoredSuggestions.Count,
                suggestions.Select(x => x.Id).ToArray(),
                suggestions.Select(x => x.MeetingTagId).ToArray());
        }

        private async Task<bool> SupersedePendingSuggestionsAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var pending = await _dbContext.MeetingTagSuggestions
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && x.Status == MeetingTagSuggestionStatus.PendingReview)
                .ToListAsync(cancellationToken);

            foreach (var suggestion in pending)
            {
                suggestion.Status = MeetingTagSuggestionStatus.Superseded;
            }

            return pending.Count > 0;
        }

        private static string BuildPrompt(
            string meetingTitle,
            string transcript,
            string? summary,
            IReadOnlyList<MeetingTag> orgTags)
        {
            var tagListJson = JsonSerializer.Serialize(
                orgTags.Select(tag => new
                {
                    id = tag.Id,
                    name = tag.Name,
                    color = tag.Color
                }),
                JsonOptions);

            return $$"""
                You are an AI meeting assistant. Suggest which predefined organization meeting tags apply to this completed meeting.

                Rules:
                - Only suggest tags from the provided organization tag list.
                - Prefer exact tag IDs. If unsure, return no suggestion instead of inventing a tag.
                - Do not create new tag names.
                - Return JSON only with this shape: {"suggested_tags":[{"id":"tag-guid","name":"existing tag name","confidence":0.0,"reason":"short reason"}]}.
                - Use confidence between 0 and 1.
                - Return an empty array when none of the predefined tags clearly apply.

                Meeting title:
                {{meetingTitle}}

                Organization predefined tags:
                {{tagListJson}}

                Meeting summary:
                {{(string.IsNullOrWhiteSpace(summary) ? "No generated summary is available." : summary.Trim())}}

                Transcript:
                {{transcript.Trim()}}
                """;
        }

        private static ParsedTagSuggestions ParseResponse(LLMResponse response, Guid meetingId)
        {
            var content = response.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"LLM returned no tag suggestion content for meeting '{meetingId}'.");
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<ParsedTagSuggestions>(content, JsonOptions)
                    ?? throw new InvalidOperationException("LLM tag suggestion JSON deserialized to null.");

                if (parsed.SuggestedTags is null)
                {
                    throw new InvalidOperationException("LLM tag suggestion JSON must contain a suggested_tags array.");
                }

                return parsed;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("LLM returned malformed tag suggestion JSON.", ex);
            }
        }

        private static ValidatedSuggestions ValidateSuggestions(
            IReadOnlyList<ParsedTagSuggestion> parsedSuggestions,
            IReadOnlyList<MeetingTag> orgTags)
        {
            var tagsById = orgTags.ToDictionary(x => x.Id);
            var tagsByName = orgTags
                .GroupBy(x => NormalizeName(x.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var valid = new List<ValidatedSuggestion>();
            var ignored = new List<IgnoredTagSuggestion>();
            var seenTagIds = new HashSet<Guid>();

            foreach (var suggestion in parsedSuggestions)
            {
                var matched = ResolveTag(suggestion, tagsById, tagsByName);
                if (matched is null)
                {
                    ignored.Add(new IgnoredTagSuggestion(
                        suggestion.Id,
                        suggestion.Name,
                        "unknown_tag"));
                    continue;
                }

                if (!NameMatchesIfProvided(suggestion.Name, matched.Name))
                {
                    ignored.Add(new IgnoredTagSuggestion(
                        suggestion.Id,
                        suggestion.Name,
                        "id_name_mismatch"));
                    continue;
                }

                if (!seenTagIds.Add(matched.Id))
                {
                    ignored.Add(new IgnoredTagSuggestion(
                        suggestion.Id,
                        suggestion.Name,
                        "duplicate_tag"));
                    continue;
                }

                valid.Add(new ValidatedSuggestion(
                    matched,
                    ClampConfidence(suggestion.Confidence),
                    suggestion.Reason));
            }

            return new ValidatedSuggestions(valid, ignored);
        }

        private static MeetingTag? ResolveTag(
            ParsedTagSuggestion suggestion,
            IReadOnlyDictionary<Guid, MeetingTag> tagsById,
            IReadOnlyDictionary<string, MeetingTag> tagsByName)
        {
            if (!string.IsNullOrWhiteSpace(suggestion.Id))
            {
                return Guid.TryParse(suggestion.Id, out var tagId)
                       && tagsById.TryGetValue(tagId, out var byId)
                    ? byId
                    : null;
            }

            var normalizedName = NormalizeName(suggestion.Name);
            return !string.IsNullOrWhiteSpace(normalizedName) && tagsByName.TryGetValue(normalizedName, out var byName)
                ? byName
                : null;
        }

        private static bool NameMatchesIfProvided(string? suggestedName, string actualName)
        {
            return string.IsNullOrWhiteSpace(suggestedName)
                   || string.Equals(NormalizeName(suggestedName), NormalizeName(actualName), StringComparison.OrdinalIgnoreCase);
        }

        private static decimal? ClampConfidence(decimal? confidence)
        {
            if (!confidence.HasValue)
            {
                return null;
            }

            return Math.Min(1m, Math.Max(0m, confidence.Value));
        }

        private static string NormalizeName(string? name)
        {
            return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private sealed record ParsedTagSuggestions(
            [property: JsonPropertyName("suggested_tags")] List<ParsedTagSuggestion> SuggestedTags);

        private sealed record ParsedTagSuggestion(
            [property: JsonPropertyName("id")] string? Id,
            [property: JsonPropertyName("name")] string? Name,
            [property: JsonPropertyName("confidence")] decimal? Confidence,
            [property: JsonPropertyName("reason")] string? Reason);

        private sealed record ValidatedSuggestion(MeetingTag Tag, decimal? Confidence, string? Reason);

        private sealed record IgnoredTagSuggestion(string? Id, string? Name, string Reason);

        private sealed record ValidatedSuggestions(
            IReadOnlyList<ValidatedSuggestion> ValidSuggestions,
            IReadOnlyList<IgnoredTagSuggestion> IgnoredSuggestions);

        private sealed record SuggestionMetadata(
            int SchemaVersion,
            string Source,
            Guid TranscriptId,
            Guid? SummaryId,
            int OrgTagCount,
            IReadOnlyList<Guid> RequestedTagIds,
            int RawSuggestedCount,
            IReadOnlyList<IgnoredTagSuggestion> IgnoredSuggestions,
            bool KnowledgeRefreshPending,
            bool UsesOnlyConfirmedTagsForRag);
    }
}
