using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Shared.Errors
{
    public static class OrganizationErrors
    {
        public static readonly Error AlreadyHasMembership = new("Organization.AlreadyHasMembership", "User already has an active organization membership.", StatusCodes.Status403Forbidden);
        public static readonly Error NotFound = new("Organization.NotFound", "Organization not found.", StatusCodes.Status404NotFound);
        public static readonly Error SlugConflict = new("Organization.SlugConflict", "Could not generate a unique slug. Please try a different name.", StatusCodes.Status409Conflict);
        public static readonly Error LastAdmin = new("Organization.LastAdmin", "Cannot remove or demote the last admin of the organization.", StatusCodes.Status403Forbidden);
        public static readonly Error MemberNotFound = new("Organization.MemberNotFound", "Member not found in this organization.", StatusCodes.Status404NotFound);
        public static readonly Error InvitationNotFound = new("Invitation.NotFound", "Invitation not found or has expired.", StatusCodes.Status404NotFound);
        public static readonly Error InvitationExpired = new("Invitation.Expired", "This invitation has expired.", StatusCodes.Status403Forbidden);
        public static readonly Error InvitationRevoked = new("Invitation.Revoked", "This invitation has been revoked.", StatusCodes.Status403Forbidden);
        public static readonly Error EmailNotWhitelisted = new("Invitation.EmailNotWhitelisted", "Your email is not on the invitation whitelist.", StatusCodes.Status403Forbidden);
        public static readonly Error Unauthorized = new("Organization.Unauthorized", "You do not have permission to perform this action.", StatusCodes.Status403Forbidden);
    }
}
