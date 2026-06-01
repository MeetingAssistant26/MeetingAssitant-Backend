using Mapster;
using System.Text;
using MeetingAssistant.Features.AgentApi.Models.Requests;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.AgentApi.Services
{
    public class AgentContextService(
        ApplicationDbContext dbContext,
        IKnowledgeRetrievalService knowledgeRetrievalService) : IAgentContextService
    {
        private const int DefaultTopK = 5;
        private const int MaxTopK = 10;
        private const int RetrievalOverflowTopK = 20;
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IKnowledgeRetrievalService _knowledgeRetrievalService = knowledgeRetrievalService;

        public async Task<Result<AgentOrganizationResponse>> GetOrganizationAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var organization = await _dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

            if (organization == null)
            {
                return Result.Failure<AgentOrganizationResponse>(OrganizationErrors.NotFound);
            }

            var memberCount = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(m => m.OrganizationId == organizationId && m.IsEnabled, cancellationToken);

            var response = organization.Adapt<AgentOrganizationResponse>() with
            {
                MemberCount = memberCount
            };

            return Result.Success(response);
        }

        public async Task<Result<IReadOnlyList<AgentMemberResponse>>> GetMeetingMembersAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meetingExists = await _dbContext.Meetings
                .AsNoTracking()
                .AnyAsync(m => m.Id == meetingId && m.OrganizationId == organizationId, cancellationToken);

            if (!meetingExists)
            {
                return Result.Failure<IReadOnlyList<AgentMemberResponse>>(MeetingErrors.NotFound);
            }

            var participants = await _dbContext.MeetingParticipants
                .AsNoTracking()
                .Include(mp => mp.User)
                .Where(mp => mp.OrganizationId == organizationId && mp.MeetingId == meetingId)
                .OrderBy(mp => mp.User.DisplayName)
                .ThenBy(mp => mp.UserId)
                .ToListAsync(cancellationToken);

            var responses = await BuildMemberResponsesAsync(
                organizationId,
                participants,
                cancellationToken);

            return Result.Success<IReadOnlyList<AgentMemberResponse>>(responses);
        }

        public async Task<Result<AgentMeetingListResponse>> ListMeetingsAsync(
            Guid organizationId,
            string? status,
            int limit,
            int offset,
            CancellationToken cancellationToken = default)
        {
            var normalizedLimit = limit <= 0 ? 20 : Math.Min(limit, 100);
            var normalizedOffset = Math.Max(offset, 0);
            var now = DateTime.UtcNow;

            IQueryable<Meeting> query = _dbContext.Meetings
                .AsNoTracking()
                .Include(m => m.Tags);

            if (string.IsNullOrWhiteSpace(status)
                || string.Equals(status, "upcoming", StringComparison.OrdinalIgnoreCase))
            {
                query = query
                    .Where(m => m.OrganizationId == organizationId)
                    .Where(m => m.Status == MeetingStatus.Scheduled || m.Status == MeetingStatus.InProgress)
                    .OrderBy(m => m.ScheduledStartUtc);
            }
            else if (string.Equals(status, "past", StringComparison.OrdinalIgnoreCase))
            {
                query = query
                    .Where(m => m.OrganizationId == organizationId)
                    .Where(m => m.Status == MeetingStatus.Completed
                                || m.Status == MeetingStatus.Cancelled
                                || m.ScheduledEndUtc < now)
                    .OrderByDescending(m => m.ScheduledStartUtc);
            }
            else
            {
                return Result.Failure<AgentMeetingListResponse>(AgentApiErrors.InvalidMeetingStatusFilter);
            }

            var totalCount = await query.CountAsync(cancellationToken);
            var meetings = await query
                .Skip(normalizedOffset)
                .Take(normalizedLimit)
                .ToListAsync(cancellationToken);

            var items = meetings.Adapt<List<AgentMeetingResponse>>();
            var response = new AgentMeetingListResponse(items, totalCount, normalizedLimit, normalizedOffset);

            return Result.Success(response);
        }

        public async Task<Result<AgentMeetingDetailResponse>> GetMeetingDetailAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meeting = await _dbContext.Meetings
                .AsNoTracking()
                .Include(m => m.Tags)
                .Include(m => m.Participants)
                    .ThenInclude(p => p.User)
                .FirstOrDefaultAsync(
                    m => m.Id == meetingId && m.OrganizationId == organizationId,
                    cancellationToken);

            if (meeting == null)
            {
                return Result.Failure<AgentMeetingDetailResponse>(MeetingErrors.NotFound);
            }

            var participants = await BuildMemberResponsesAsync(
                organizationId,
                meeting.Participants.OrderBy(p => p.User.DisplayName).ThenBy(p => p.UserId).ToList(),
                cancellationToken);

            var response = meeting.Adapt<AgentMeetingDetailResponse>() with
            {
                Participants = participants
            };

            return Result.Success(response);
        }

        public async Task<Result<IReadOnlyList<AgentMeetingResponse>>> ListRecurringMeetingsAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var meetings = await _dbContext.Meetings
                .AsNoTracking()
                .Include(m => m.Tags)
                .Where(m => m.OrganizationId == organizationId && m.RecurrenceConfig != null)
                .OrderBy(m => m.ScheduledStartUtc)
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<AgentMeetingResponse>>(
                meetings.Adapt<List<AgentMeetingResponse>>());
        }

        public async Task<Result<IReadOnlyList<MeetingTagResponse>>> ListMeetingTagsAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var tags = await _dbContext.MeetingTags
                .AsNoTracking()
                .Where(t => t.OrganizationId == organizationId && t.IsActive)
                .OrderBy(t => t.Name)
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<MeetingTagResponse>>(tags.Adapt<List<MeetingTagResponse>>());
        }

        public async Task<Result<AgentMeetingContextQueryResponse>> QueryMeetingContextAsync(
            AgentMeetingContextQueryRequest request,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meetingExists = await _dbContext.Meetings
                .AsNoTracking()
                .AnyAsync(m => m.Id == meetingId && m.OrganizationId == organizationId, cancellationToken);

            if (!meetingExists)
            {
                return Result.Failure<AgentMeetingContextQueryResponse>(MeetingErrors.NotFound);
            }

            var topK = NormalizeTopK(request.TopK);
            var sourceTypes = NormalizeSourceTypes(request.SourceTypes);
            var retrievalTopK = sourceTypes.Count > 0 ? RetrievalOverflowTopK : topK;

            var results = await _knowledgeRetrievalService.RetrieveAsync(
                new KnowledgeRetrievalRequest(
                    organizationId,
                    BuildQueryText(request.Question, request.Transcript),
                    retrievalTopK,
                    meetingId,
                    NormalizePreferredTagIds(request.PreferredTagIds)),
                cancellationToken);

            var snippets = results
                .Where(result => sourceTypes.Count == 0 || sourceTypes.Contains(result.SourceType))
                .Take(topK)
                .Select(MapSnippet)
                .ToList();

            return Result.Success(new AgentMeetingContextQueryResponse(meetingId, topK, snippets));
        }

        private async Task<List<AgentMemberResponse>> BuildMemberResponsesAsync(
            Guid organizationId,
            IReadOnlyCollection<MeetingParticipant> participants,
            CancellationToken cancellationToken)
        {
            var userIds = participants.Select(p => p.UserId).Distinct().ToList();

            var memberships = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.OrganizationId == organizationId && userIds.Contains(m.UserId) && m.IsEnabled)
                .ToDictionaryAsync(m => m.UserId, cancellationToken);

            return participants
                .Select(participant =>
                {
                    memberships.TryGetValue(participant.UserId, out var membership);
                    return participant.Adapt<AgentMemberResponse>() with
                    {
                        JobRole = membership?.JobRole,
                        Context = membership?.Context
                    };
                })
                .ToList();
        }

        private static int NormalizeTopK(int? topK) => Math.Clamp(topK ?? DefaultTopK, 1, MaxTopK);

        private static IReadOnlyCollection<Guid>? NormalizePreferredTagIds(IReadOnlyCollection<Guid>? preferredTagIds)
        {
            var normalized = preferredTagIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray() ?? [];

            return normalized.Length == 0 ? null : normalized;
        }

        private static HashSet<KnowledgeArtifactType> NormalizeSourceTypes(IReadOnlyCollection<string>? sourceTypes)
        {
            if (sourceTypes is null || sourceTypes.Count == 0)
            {
                return [];
            }

            return sourceTypes
                .Where(type => Enum.TryParse<KnowledgeArtifactType>(type, ignoreCase: true, out _))
                .Select(type => Enum.Parse<KnowledgeArtifactType>(type, ignoreCase: true))
                .ToHashSet();
        }

        private static string BuildQueryText(string question, string? transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return question.Trim();
            }

            var builder = new StringBuilder();
            builder.AppendLine("Question:");
            builder.AppendLine(question.Trim());
            builder.AppendLine();
            builder.AppendLine("Current transcript/context:");
            builder.Append(transcript.Trim());
            return builder.ToString();
        }

        private static AgentMeetingContextSnippetResponse MapSnippet(KnowledgeRetrievalResult result)
        {
            return new AgentMeetingContextSnippetResponse(
                result.ChunkId,
                result.DocumentId,
                new AgentMeetingContextSourceResponse(
                    result.SourceMeetingId,
                    result.SourceMeetingTitle,
                    result.SourceMeetingScheduledStartUtc,
                    result.SourceType.ToString()),
                result.DocumentTitle,
                result.ChunkText,
                result.Tags
                    .Select(tag => new AgentMeetingContextTagResponse(tag.Id, tag.Name, tag.Color))
                    .ToList(),
                new AgentMeetingContextScoreResponse(
                    result.VectorDistance,
                    result.SharedTagCount,
                    result.TagBoost,
                    result.RankingScore,
                    result.IsTagPreferredResult));
        }
    }
}
