using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Services.TagSuggestions;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.TagSuggestions
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/tag-suggestions")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class MeetingTagSuggestionController(
        IMeetingTagSuggestionReviewService tagSuggestionReviewService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IMeetingTagSuggestionReviewService _tagSuggestionReviewService = tagSuggestionReviewService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
