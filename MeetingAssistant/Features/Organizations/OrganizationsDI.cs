using MeetingAssistant.Features.Organizations.Services;

namespace MeetingAssistant.Features.Organizations
{
    public static class OrganizationsDI
    {
        public static IServiceCollection AddOrganizationsFeature(this IServiceCollection services)
        {
            services.AddScoped<ISlugGenerator, SlugGenerator>();
            services.AddScoped<IOrganizationService, OrganizationService>();
            services.AddScoped<IMemberService, MemberService>();
            services.AddScoped<IInvitationService, InvitationService>();
            services.AddScoped<IMeetingTagService, MeetingTagService>();

            return services;
        }
    }
}
