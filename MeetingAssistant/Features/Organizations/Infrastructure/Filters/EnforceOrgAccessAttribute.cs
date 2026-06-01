using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Organizations.Infrastructure.Filters
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class EnforceOrgAccessAttribute : Attribute, IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!context.RouteData.Values.TryGetValue("orgId", out var routeOrgId)
                || !Guid.TryParse(routeOrgId?.ToString(), out var parsedOrgId))
            {
                await next();
                return;
            }

            var tenantProvider = context.HttpContext.RequestServices.GetRequiredService<ITenantProvider>();

            if (tenantProvider.CurrentOrganizationId != parsedOrgId)
            {
                context.Result = new ForbidResult();
                return;
            }

            var userIdClaim = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                context.Result = new ForbidResult();
                return;
            }

            var dbContext = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
            var isActiveMember = await dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AnyAsync(m => m.UserId == userId && m.OrganizationId == parsedOrgId && m.IsEnabled);

            if (!isActiveMember)
            {
                context.Result = new ForbidResult();
                return;
            }

            await next();
        }
    }
}
