using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Features.ActionItems.Services.Abstractions;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.ActionItems.Services
{
    public class UserIntegrationService : IUserIntegrationService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ITaskProviderFactory _providerFactory;
        private readonly IDataProtector _dataProtector;
        private readonly ILogger<UserIntegrationService> _logger;

        public UserIntegrationService(
            ApplicationDbContext dbContext,
            ITaskProviderFactory providerFactory,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<UserIntegrationService> logger)
        {
            _dbContext = dbContext;
            _providerFactory = providerFactory;
            _dataProtector = dataProtectionProvider.CreateProtector("ActionItemsIntegration");
            _logger = logger;
        }

        public async Task<Result<List<UserIntegrationResponse>>> GetMyConnectionsAsync(
            Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var links = await _dbContext.ExternalAccountLinks
                .Where(l => l.UserId == userId && l.OrganizationId == organizationId)
                .ToListAsync(cancellationToken);

            var result = links.Select(l => new UserIntegrationResponse
            {
                Provider = l.Provider.ToString(),
                IsConnected = true,
                ExternalUsername = l.ExternalUsername
            }).ToList();

            return Result.Success(result);
        }

        public async Task<Result> ConnectAsync(
            ConnectProviderRequest request,
            Guid userId, Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var providerImpl = _providerFactory.GetProvider(provider.ToString());

            var protectedToken = _dataProtector.Protect(request.Token);

            // Validate token by calling provider
            var orgConfig = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (orgConfig == null)
                return Result.Failure(new Error("BadRequest", "Organization integration not configured for this provider.", 400));

            // Create a temporary config with the user's token for validation
            // For Trello, we need the org's API key + user's token
            var tempPayload = System.Text.Json.JsonSerializer.Serialize(new { apiKey = ExtractApiKey(orgConfig), apiToken = request.Token });
            var encryptedTempPayload = _dataProtector.Protect(tempPayload);
            var tempConfig = new OrganizationIntegrationConfig
            {
                OrganizationId = organizationId,
                Provider = provider,
                SelectedProjectId = orgConfig.SelectedProjectId,
                SelectedListId = orgConfig.SelectedListId,
                EncryptedProviderPayload = encryptedTempPayload
            };

            var healthResult = await providerImpl.ValidateCredentialsAsync(tempConfig, cancellationToken);
            if (!healthResult.IsHealthy)
                return Result.Failure(new Error("Unauthorized", $"Invalid token: {healthResult.ErrorMessage}", 401));

            var existingLink = await _dbContext.ExternalAccountLinks
                .FirstOrDefaultAsync(l => l.UserId == userId && l.OrganizationId == organizationId && l.Provider == provider, cancellationToken);

            if (existingLink != null)
            {
                existingLink.AccessTokenProtected = protectedToken;
                existingLink.ExternalUserId = healthResult.ExternalUserId ?? "unknown";
                existingLink.ExternalUsername = healthResult.ExternalUsername;
                existingLink.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.ExternalAccountLinks.Add(new ExternalAccountLink
                {
                    OrganizationId = organizationId,
                    UserId = userId,
                    Provider = provider,
                    ExternalUserId = healthResult.ExternalUserId ?? "unknown",
                    ExternalUsername = healthResult.ExternalUsername,
                    AccessTokenProtected = protectedToken
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result> DisconnectAsync(
            Guid userId, Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var link = await _dbContext.ExternalAccountLinks
                .FirstOrDefaultAsync(l => l.UserId == userId && l.OrganizationId == organizationId && l.Provider == provider, cancellationToken);

            if (link != null)
            {
                _dbContext.ExternalAccountLinks.Remove(link);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Success();
        }

        private string ExtractApiKey(OrganizationIntegrationConfig config)
        {
            var payload = _dataProtector.Unprotect(config.EncryptedProviderPayload);
            var doc = System.Text.Json.JsonDocument.Parse(payload);
            return doc.RootElement.GetProperty("apiKey").GetString() ?? string.Empty;
        }
    }
}
