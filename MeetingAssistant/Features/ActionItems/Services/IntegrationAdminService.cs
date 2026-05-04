using System.Text.Json;
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
    public class IntegrationAdminService : IIntegrationAdminService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ITaskProviderFactory _providerFactory;
        private readonly IDataProtector _dataProtector;
        private readonly ILogger<IntegrationAdminService> _logger;

        public IntegrationAdminService(
            ApplicationDbContext dbContext,
            ITaskProviderFactory providerFactory,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<IntegrationAdminService> logger)
        {
            _dbContext = dbContext;
            _providerFactory = providerFactory;
            _dataProtector = dataProtectionProvider.CreateProtector("ActionItemsIntegration");
            _logger = logger;
        }

        public async Task<Result<IntegrationConfigResponse>> GetConfigAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var integration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Type == provider, cancellationToken);

            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            return Result.Success(new IntegrationConfigResponse
            {
                IsConfigured = config != null,
                IntegrationStatus = integration?.Status.ToString() ?? IntegrationStatus.Disabled.ToString(),
                ProjectId = config?.SelectedProjectId,
                ListId = config?.SelectedListId
            });
        }

        public async Task<Result> SaveConfigAsync(
            SaveIntegrationConfigRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var providerImpl = _providerFactory.GetProvider(provider.ToString());

            var payload = JsonSerializer.Serialize(new { apiKey = request.ApiKey, apiToken = request.ApiToken });
            var encryptedPayload = _dataProtector.Protect(payload);

            var tempConfig = new OrganizationIntegrationConfig
            {
                OrganizationId = organizationId,
                Provider = provider,
                SelectedProjectId = request.ProjectId,
                SelectedListId = request.ListId,
                EncryptedProviderPayload = encryptedPayload
            };

            var healthResult = await providerImpl.ValidateCredentialsAsync(tempConfig, cancellationToken);
            if (!healthResult.IsHealthy)
                return Result.Failure(new Error("Unauthorized", $"Invalid credentials: {healthResult.ErrorMessage}", 401));

            var existingConfig = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (existingConfig != null)
            {
                existingConfig.SelectedProjectId = request.ProjectId;
                existingConfig.SelectedListId = request.ListId;
                existingConfig.EncryptedProviderPayload = encryptedPayload;
                existingConfig.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.OrganizationIntegrationConfigs.Add(tempConfig);
            }

            var existingIntegration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Type == provider, cancellationToken);

            if (existingIntegration != null)
            {
                existingIntegration.Status = IntegrationStatus.Active;
                existingIntegration.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.OrganizationIntegrations.Add(new OrganizationIntegration
                {
                    OrganizationId = organizationId,
                    Type = provider,
                    Status = IntegrationStatus.Active
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result<List<ProviderProjectResponse>>> GetProjectsAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (config == null)
                return Result.Failure<List<ProviderProjectResponse>>(new Error("BadRequest", "Integration not configured.", 400));

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var projects = await providerImpl.ListProjectsAsync(config, cancellationToken);

            return Result.Success(projects.Select(p => new ProviderProjectResponse { Id = p.Id, Name = p.Name }).ToList());
        }

        public async Task<Result<List<ProviderListResponse>>> GetListsAsync(
            Guid organizationId, ExternalProvider provider, string projectId,
            CancellationToken cancellationToken = default)
        {
            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (config == null)
                return Result.Failure<List<ProviderListResponse>>(new Error("BadRequest", "Integration not configured.", 400));

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var lists = await providerImpl.ListListsAsync(config, projectId, cancellationToken);

            return Result.Success(lists.Select(l => new ProviderListResponse { Id = l.Id, Name = l.Name }).ToList());
        }

        public async Task<Result<List<MemberProviderStatusResponse>>> GetMemberStatusAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var memberships = await _dbContext.UserOrgMemberships
                .Where(m => m.OrganizationId == organizationId)
                .Select(m => new { m.UserId, m.User.UserName, m.User.DisplayName })
                .ToListAsync(cancellationToken);

            var connections = await _dbContext.ExternalAccountLinks
                .Where(l => l.OrganizationId == organizationId && l.Provider == provider)
                .ToListAsync(cancellationToken);

            var mappings = await _dbContext.ExternalMemberMappings
                .Where(m => m.OrganizationId == organizationId && m.Provider == provider)
                .ToListAsync(cancellationToken);

            var result = memberships.Select(m => new MemberProviderStatusResponse
            {
                UserId = m.UserId,
                DisplayName = m.DisplayName ?? m.UserName ?? "Unknown",
                IsConnected = connections.Any(c => c.UserId == m.UserId),
                ExternalUsername = connections.FirstOrDefault(c => c.UserId == m.UserId)?.ExternalUsername,
                AdminMappedMemberId = mappings.FirstOrDefault(map => map.UserId == m.UserId)?.ExternalMemberId
            }).ToList();

            return Result.Success(result);
        }

        public async Task<Result> SetMemberMappingAsync(
            SetMemberMappingRequest request,
            Guid organizationId, ExternalProvider provider, Guid userId,
            CancellationToken cancellationToken = default)
        {
            var existing = await _dbContext.ExternalMemberMappings
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.Provider == provider, cancellationToken);

            if (existing != null)
            {
                existing.ExternalMemberId = request.ExternalMemberId;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.ExternalMemberMappings.Add(new ExternalMemberMapping
                {
                    OrganizationId = organizationId,
                    UserId = userId,
                    Provider = provider,
                    ExternalMemberId = request.ExternalMemberId
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
