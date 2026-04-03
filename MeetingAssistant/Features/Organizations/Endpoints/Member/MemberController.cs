using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Organizations.Services;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Member
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/members")]
    public partial class MemberController(
        IMemberService memberService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IMemberService _memberService = memberService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}