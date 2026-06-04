using System.Text.Json;
using System.Text.Json.Nodes;
using Hangfire;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Meetings.Services.TagSuggestions
{
    public interface IMeetingTagSuggestionReviewService
    {
        Task<Result<MeetingTagSuggestionListResponse>> ListAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingTagSuggestionResponse>> UpdateAsync(
            Guid organizationId,
            Guid meetingId,
            Guid suggestionId,
            Guid callerUserId,
            UpdateMeetingTagSuggestionRequest request,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingTagSuggestionResponse>> RejectAsync(
            Guid organizationId,
            Guid meetingId,
            Guid suggestionId,
            Guid callerUserId,
            RejectMeetingTagSuggestionRequest request,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingTagSuggestionApplyResponse>> ApplyAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            ApplyMeetingTagSuggestionsRequest request,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingTagMetadataRefreshResponse>> RefreshTagMetadataAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default);
    }

    public sealed class MeetingTagSuggestionReviewService(
        ApplicationDbContext dbContext,
        IBackgroundJobClient backgroundJobClient,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null) : IMeetingTagSuggestionReviewService
    {
        private const int MaxReasonLength = 1000;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        public async Task<Result<MeetingTagSuggestionListResponse>> ListAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default)
        {
            var access = await EnsureCanViewAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access.IsFailure)
            {
                return Result.Failure<MeetingTagSuggestionListResponse>(access.Error);
            }

            return Result.Success(await BuildListResponseAsync(organizationId, meetingId, cancellationToken));
        }

        public async Task<Result<MeetingTagSuggestionResponse>> UpdateAsync(
            Guid organizationId,
            Guid meetingId,
            Guid suggestionId,
            Guid callerUserId,
            UpdateMeetingTagSuggestionRequest request,
            CancellationToken cancellationToken = default)
        {
            var access = await EnsureCanModifyAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access.IsFailure)
            {
                return Result.Failure<MeetingTagSuggestionResponse>(access.Error);
            }

            var suggestion = await LoadSuggestionForUpdateAsync(organizationId, meetingId, suggestionId, cancellationToken);
            if (suggestion is null)
            {
                return Result.Failure<MeetingTagSuggestionResponse>(MeetingTagSuggestionErrors.NotFound);
            }

            if (suggestion.Status != MeetingTagSuggestionStatus.PendingReview)
            {
                return Result.Failure<MeetingTagSuggestionResponse>(MeetingTagSuggestionErrors.InvalidState);
            }

            if (request.MeetingTagId.HasValue && request.MeetingTagId.Value != suggestion.MeetingTagId)
            {
                var replacementTag = await _dbContext.MeetingTags
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.Id == request.MeetingTagId.Value
                             && x.OrganizationId == organizationId
                             && x.IsActive,
                        cancellationToken);

                if (replacementTag is null)
                {
                    return Result.Failure<MeetingTagSuggestionResponse>(MeetingTagSuggestionErrors.InvalidTag);
                }

                suggestion.MeetingTagId = replacementTag.Id;
                suggestion.TagNameSnapshot = replacementTag.Name;
                suggestion.TagColorSnapshot = replacementTag.Color;
            }

            if (request.Confidence.HasValue)
            {
                suggestion.Confidence = Math.Min(1m, Math.Max(0m, request.Confidence.Value));
            }

            if (request.Reason is not null)
            {
                suggestion.Reason = Truncate(request.Reason, MaxReasonLength);
            }

            suggestion.MetadataJson = WriteReviewMetadata(
                suggestion.MetadataJson,
                "updated",
                callerUserId,
                request.Reason,
                knowledgeRefreshPending: false,
                reindexJobId: null);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(MapSuggestion(suggestion));
        }

        public async Task<Result<MeetingTagSuggestionResponse>> RejectAsync(
            Guid organizationId,
            Guid meetingId,
            Guid suggestionId,
            Guid callerUserId,
            RejectMeetingTagSuggestionRequest request,
            CancellationToken cancellationToken = default)
        {
            var access = await EnsureCanModifyAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access.IsFailure)
            {
                return Result.Failure<MeetingTagSuggestionResponse>(access.Error);
            }

            var suggestion = await LoadSuggestionForUpdateAsync(organizationId, meetingId, suggestionId, cancellationToken);
            if (suggestion is null)
            {
                return Result.Failure<MeetingTagSuggestionResponse>(MeetingTagSuggestionErrors.NotFound);
            }

            if (suggestion.Status != MeetingTagSuggestionStatus.PendingReview)
            {
                return Result.Failure<MeetingTagSuggestionResponse>(MeetingTagSuggestionErrors.InvalidState);
            }

            suggestion.Status = MeetingTagSuggestionStatus.Rejected;
            if (!string.IsNullOrWhiteSpace(request.Reason))
            {
                suggestion.Reason = Truncate(request.Reason, MaxReasonLength);
            }

            suggestion.MetadataJson = WriteReviewMetadata(
                suggestion.MetadataJson,
                "rejected",
                callerUserId,
                request.Reason,
                knowledgeRefreshPending: false,
                reindexJobId: null);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(MapSuggestion(suggestion));
        }

        public async Task<Result<MeetingTagSuggestionApplyResponse>> ApplyAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            ApplyMeetingTagSuggestionsRequest request,
            CancellationToken cancellationToken = default)
        {
            var access = await EnsureCanModifyAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access.IsFailure)
            {
                return Result.Failure<MeetingTagSuggestionApplyResponse>(access.Error);
            }

            var selectedTagIds = (request.TagIds ?? [])
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();

            var activeSelectedTags = await _dbContext.MeetingTags
                .IgnoreQueryFilters()
                .Where(x => selectedTagIds.Contains(x.Id) && x.OrganizationId == organizationId && x.IsActive)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            if (activeSelectedTags.Count != selectedTagIds.Length)
            {
                return Result.Failure<MeetingTagSuggestionApplyResponse>(MeetingTagSuggestionErrors.InvalidTag);
            }

            var selectedSuggestionIds = (request.SuggestionIds ?? [])
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();

            var suggestions = await _dbContext.MeetingTagSuggestions
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderBy(x => x.SuggestedAtUtc)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            if (selectedSuggestionIds.Length > 0)
            {
                var selectedSuggestions = suggestions
                    .Where(x => selectedSuggestionIds.Contains(x.Id))
                    .ToList();

                if (selectedSuggestions.Count != selectedSuggestionIds.Length
                    || selectedSuggestions.Any(x => !selectedTagIds.Contains(x.MeetingTagId)))
                {
                    return Result.Failure<MeetingTagSuggestionApplyResponse>(MeetingTagSuggestionErrors.InvalidSuggestionSelection);
                }
            }

            var currentTags = await _dbContext.MeetingMeetingTags
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId)
                .ToListAsync(cancellationToken);

            var currentTagIds = currentTags.Select(x => x.MeetingTagId).ToHashSet();
            var selectedTagIdSet = selectedTagIds.ToHashSet();

            var toRemove = currentTags.Where(x => !selectedTagIdSet.Contains(x.MeetingTagId)).ToList();
            if (toRemove.Count > 0)
            {
                _dbContext.MeetingMeetingTags.RemoveRange(toRemove);
            }

            var toAdd = selectedTagIds
                .Where(x => !currentTagIds.Contains(x))
                .Select(x => new MeetingMeetingTag
                {
                    MeetingId = meetingId,
                    MeetingTagId = x
                })
                .ToList();
            if (toAdd.Count > 0)
            {
                _dbContext.MeetingMeetingTags.AddRange(toAdd);
            }

            var now = DateTime.UtcNow;
            var selectedSuggestionIdSet = selectedSuggestionIds.ToHashSet();
            var confirmedSuggestionIds = new List<Guid>();
            var changedSuggestionState = false;

            foreach (var suggestion in suggestions)
            {
                var selectedByTag = selectedTagIdSet.Contains(suggestion.MeetingTagId)
                                    && (selectedSuggestionIds.Length == 0 || selectedSuggestionIdSet.Contains(suggestion.Id));

                if (selectedByTag && suggestion.Status != MeetingTagSuggestionStatus.Confirmed)
                {
                    suggestion.Status = MeetingTagSuggestionStatus.Confirmed;
                    suggestion.MetadataJson = WriteReviewMetadata(
                        suggestion.MetadataJson,
                        "confirmed",
                        callerUserId,
                        note: null,
                        knowledgeRefreshPending: true,
                        reindexJobId: null,
                        reviewedAtUtc: now);
                    changedSuggestionState = true;
                }

                if (selectedByTag)
                {
                    confirmedSuggestionIds.Add(suggestion.Id);
                    continue;
                }

                if (request.RejectUnselectedPending && suggestion.Status == MeetingTagSuggestionStatus.PendingReview)
                {
                    suggestion.Status = MeetingTagSuggestionStatus.Rejected;
                    suggestion.MetadataJson = WriteReviewMetadata(
                        suggestion.MetadataJson,
                        "rejected_unselected",
                        callerUserId,
                        note: null,
                        knowledgeRefreshPending: false,
                        reindexJobId: null,
                        reviewedAtUtc: now);
                    changedSuggestionState = true;
                }
                else if (suggestion.Status == MeetingTagSuggestionStatus.Confirmed && !selectedTagIdSet.Contains(suggestion.MeetingTagId))
                {
                    suggestion.Status = MeetingTagSuggestionStatus.Rejected;
                    suggestion.MetadataJson = WriteReviewMetadata(
                        suggestion.MetadataJson,
                        "rejected_removed_from_final_tags",
                        callerUserId,
                        note: null,
                        knowledgeRefreshPending: true,
                        reindexJobId: null,
                        reviewedAtUtc: now);
                    changedSuggestionState = true;
                }
            }

            var tagAssociationsChanged = toRemove.Count > 0 || toAdd.Count > 0;
            string? reindexJobId = null;
            if (tagAssociationsChanged || changedSuggestionState)
            {
                reindexJobId = await EnqueueKnowledgeReindexAsync(
                    meetingId,
                    organizationId,
                    "Knowledge tag metadata refresh job enqueued after tag suggestion review.",
                    manualRerun: true,
                    cancellationToken);
                foreach (var suggestion in suggestions.Where(x => confirmedSuggestionIds.Contains(x.Id)))
                {
                    suggestion.MetadataJson = WriteReviewMetadata(
                        suggestion.MetadataJson,
                        "confirmed",
                        callerUserId,
                        note: null,
                        knowledgeRefreshPending: true,
                        reindexJobId: reindexJobId,
                        reviewedAtUtc: now);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var refreshedSuggestions = await _dbContext.MeetingTagSuggestions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderBy(x => x.SuggestedAtUtc)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            return Result.Success(new MeetingTagSuggestionApplyResponse(
                selectedTagIds,
                refreshedSuggestions.Select(MapSuggestion).ToList(),
                reindexJobId,
                reindexJobId is not null));
        }

        public async Task<Result<MeetingTagMetadataRefreshResponse>> RefreshTagMetadataAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default)
        {
            var access = await EnsureCanModifyAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access.IsFailure)
            {
                return Result.Failure<MeetingTagMetadataRefreshResponse>(access.Error);
            }

            var jobId = await EnqueueKnowledgeReindexAsync(
                meetingId,
                organizationId,
                "Knowledge tag metadata refresh job enqueued after tag suggestion review.",
                manualRerun: true,
                cancellationToken);

            return Result.Success(new MeetingTagMetadataRefreshResponse(jobId, true));
        }

        private async Task<MeetingTagSuggestionListResponse> BuildListResponseAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var suggestions = await _dbContext.MeetingTagSuggestions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderByDescending(x => x.Status == MeetingTagSuggestionStatus.PendingReview)
                .ThenByDescending(x => x.SuggestedAtUtc)
                .ThenBy(x => x.TagNameSnapshot)
                .ToListAsync(cancellationToken);

            var confirmedTagIds = await _dbContext.MeetingMeetingTags
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.MeetingId == meetingId && x.MeetingTag.OrganizationId == organizationId)
                .OrderBy(x => x.MeetingTag.Name)
                .Select(x => x.MeetingTagId)
                .ToListAsync(cancellationToken);

            var mapped = suggestions.Select(MapSuggestion).ToList();
            return new MeetingTagSuggestionListResponse(
                mapped,
                confirmedTagIds,
                mapped.Any(x => x.KnowledgeRefreshPending));
        }

        private async Task<MeetingTagSuggestion?> LoadSuggestionForUpdateAsync(
            Guid organizationId,
            Guid meetingId,
            Guid suggestionId,
            CancellationToken cancellationToken)
        {
            return await _dbContext.MeetingTagSuggestions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    x => x.Id == suggestionId
                         && x.OrganizationId == organizationId
                         && x.MeetingId == meetingId,
                    cancellationToken);
        }

        private async Task<Result> EnsureCanViewAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken)
        {
            var access = await LoadAccessAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access is null)
            {
                return Result.Failure(MeetingTagSuggestionErrors.MeetingNotFound);
            }

            return access.IsOrganizationAdmin || access.MeetingRole.HasValue
                ? Result.Success()
                : Result.Failure(MeetingTagSuggestionErrors.Forbidden);
        }

        private async Task<Result> EnsureCanModifyAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken)
        {
            var access = await LoadAccessAsync(organizationId, meetingId, callerUserId, cancellationToken);
            if (access is null)
            {
                return Result.Failure(MeetingTagSuggestionErrors.MeetingNotFound);
            }

            return access.IsOrganizationAdmin || access.MeetingRole is MeetingRole.Host or MeetingRole.CoHost
                ? Result.Success()
                : Result.Failure(MeetingTagSuggestionErrors.ModifyForbidden);
        }

        private async Task<AccessSnapshot?> LoadAccessAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken)
        {
            return await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(x => x.Id == meetingId && x.OrganizationId == organizationId)
                .Select(x => new AccessSnapshot(
                    _dbContext.UserOrgMemberships
                        .IgnoreQueryFilters()
                        .Any(m => m.OrganizationId == organizationId
                                  && m.UserId == callerUserId
                                  && m.IsEnabled
                                  && m.OrgRole == OrganizationRole.Admin),
                    _dbContext.MeetingParticipants
                        .IgnoreQueryFilters()
                        .Where(p => p.OrganizationId == organizationId
                                    && p.MeetingId == meetingId
                                    && p.UserId == callerUserId)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault()))
                .FirstOrDefaultAsync(cancellationToken);
        }

        private async Task<string> EnqueueKnowledgeReindexAsync(
            Guid meetingId,
            Guid organizationId,
            string message,
            bool manualRerun,
            CancellationToken cancellationToken)
        {
            Guid? pipelineGenerationId = null;
            if (_postMeetingProcessingTracker is not null)
            {
                pipelineGenerationId = manualRerun
                    ? await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                        _postMeetingProcessingTracker,
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.KnowledgeIndexing,
                        message: message,
                        cancellationToken: cancellationToken)
                    : await PostMeetingProcessingPipeline.ResolveAutomaticPipelineGenerationIdAsync(
                        _postMeetingProcessingTracker,
                        organizationId,
                        meetingId,
                        cancellationToken);
            }

            var jobId = _backgroundJobClient.Enqueue<ReindexMeetingKnowledgeJob>(
                job => job.RunAsync(meetingId, organizationId, pipelineGenerationId, CancellationToken.None));

            if (_postMeetingProcessingTracker is not null && pipelineGenerationId.HasValue)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    pipelineGenerationId,
                    message: message,
                    relatedHangfireJobId: jobId,
                    cancellationToken: cancellationToken);
            }

            return jobId;
        }

        private static MeetingTagSuggestionResponse MapSuggestion(MeetingTagSuggestion suggestion)
        {
            var metadata = ReadMetadataFlags(suggestion.MetadataJson);
            return new MeetingTagSuggestionResponse(
                suggestion.Id,
                suggestion.MeetingId,
                suggestion.MeetingTagId,
                suggestion.TagNameSnapshot,
                suggestion.TagColorSnapshot,
                suggestion.Confidence,
                suggestion.Reason,
                suggestion.Status.ToString(),
                suggestion.SuggestedAtUtc,
                suggestion.TranscriptId,
                suggestion.SummaryId,
                metadata.KnowledgeRefreshPending,
                metadata.UsesOnlyConfirmedTagsForRag);
        }

        private static MetadataFlags ReadMetadataFlags(string metadataJson)
        {
            try
            {
                var node = JsonNode.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
                return new MetadataFlags(
                    node?["knowledgeRefreshPending"]?.GetValue<bool>() ?? false,
                    node?["usesOnlyConfirmedTagsForRag"]?.GetValue<bool>() ?? true);
            }
            catch (JsonException)
            {
                return new MetadataFlags(false, true);
            }
        }

        private static string WriteReviewMetadata(
            string metadataJson,
            string action,
            Guid reviewedByUserId,
            string? note,
            bool knowledgeRefreshPending,
            string? reindexJobId,
            DateTime? reviewedAtUtc = null)
        {
            JsonObject metadata;
            try
            {
                metadata = JsonNode.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson) as JsonObject
                           ?? new JsonObject();
            }
            catch (JsonException)
            {
                metadata = new JsonObject();
            }

            metadata["knowledgeRefreshPending"] = knowledgeRefreshPending;
            metadata["usesOnlyConfirmedTagsForRag"] = true;
            metadata["review"] = new JsonObject
            {
                ["action"] = action,
                ["reviewedByUserId"] = reviewedByUserId,
                ["reviewedAtUtc"] = (reviewedAtUtc ?? DateTime.UtcNow).ToString("O"),
                ["note"] = note,
                ["reindexJobId"] = reindexJobId
            };

            return metadata.ToJsonString(JsonOptions);
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private sealed record AccessSnapshot(bool IsOrganizationAdmin, MeetingRole? MeetingRole);

        private sealed record MetadataFlags(bool KnowledgeRefreshPending, bool UsesOnlyConfirmedTagsForRag);
    }
}
