using System;
using Microsoft.AspNetCore.Http;

namespace MeetingAssistant.Api.Infrastructure.Services
{
    public class TenantProvider : ITenantProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public TenantProvider(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public Guid? CurrentOrganizationId
        {
            get
            {
                var ctx = _httpContextAccessor.HttpContext;
                if (ctx?.User?.Identity?.IsAuthenticated != true)
                    return null;

                // Look for claim named "org" or "organization"
                var claim = ctx.User.FindFirst("organizationId");
                if (claim == null)
                    return null;

                if (Guid.TryParse(claim.Value, out var guid))
                    return guid;

                return null;
            }
        }
    }
}
