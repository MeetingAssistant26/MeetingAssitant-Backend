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

            var hasCredentials = config != null && !string.IsNullOrEmpty(config.EncryptedProviderPayload);
            var isConfigured = hasCredentials
                && integration?.Status == IntegrationStatus.Active
                && !string.IsNullOrEmpty(config!.SelectedProjectId)
                && !string.IsNullOrEmpty(config.SelectedListId);

            var payload = hasCredentials ? ReadPayload(config!.EncryptedProviderPayload) : null;
            var response = new IntegrationConfigResponse
            {
                HasCredentials = hasCredentials,
                IsConfigured = isConfigured,
                IntegrationStatus = integration?.Status.ToString() ?? IntegrationStatus.Disabled.ToString(),
                WorkspaceId = payload?.WorkspaceId,
                WorkspaceName = payload?.WorkspaceName,
                ProjectId = config?.SelectedProjectId,
                ListId = config?.SelectedListId
            };

            if (config != null && hasCredentials)
            {
                try
                {
                    var providerImpl = _providerFactory.GetProvider(provider.ToString());

                    if (!string.IsNullOrEmpty(config.SelectedProjectId))
                    {
                        var projects = await providerImpl.ListProjectsAsync(config, cancellationToken);
                        response.ProjectName = projects.FirstOrDefault(p => p.Id == config.SelectedProjectId)?.Name;
                    }

                    if (!string.IsNullOrEmpty(config.SelectedProjectId) && !string.IsNullOrEmpty(config.SelectedListId))
                    {
                        var lists = await providerImpl.ListListsAsync(config, config.SelectedProjectId, cancellationToken);
                        response.ListName = lists.FirstOrDefault(l => l.Id == config.SelectedListId)?.Name;
                    }

                    if (string.IsNullOrEmpty(response.WorkspaceName) && !string.IsNullOrEmpty(payload?.WorkspaceId))
                    {
                        var workspaces = await providerImpl.ListWorkspacesAsync(config, cancellationToken);
                        response.WorkspaceName = workspaces.FirstOrDefault(w => w.Id == payload.WorkspaceId)?.Name
                            ?? workspaces.FirstOrDefault(w => w.Id == payload.WorkspaceId)?.DisplayName;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to enrich integration config names from provider for org {OrganizationId}", organizationId);
                }
            }

            return Result.Success(response);
        }

        public async Task<Result> SaveConfigAsync(
            SaveIntegrationConfigRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var providerImpl = _providerFactory.GetProvider(provider.ToString());

            var existingConfig = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            var existingPayload = existingConfig != null
                ? ReadPayload(existingConfig.EncryptedProviderPayload)
                : null;

            var payload = SerializePayload(
                request.ApiKey,
                request.ApiToken,
                existingPayload?.WorkspaceId,
                existingPayload?.WorkspaceName);

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

            string? workspaceId = existingPayload?.WorkspaceId;
            string? workspaceName = existingPayload?.WorkspaceName;

            try
            {
                var workspaces = await providerImpl.ListWorkspacesAsync(tempConfig, cancellationToken);
                foreach (var workspace in workspaces)
                {
                    var boards = await providerImpl.ListBoardsAsync(tempConfig, workspace.Id, openOnly: false, cancellationToken);
                    if (boards.Any(b => b.Id == request.ProjectId))
                    {
                        workspaceId = workspace.Id;
                        workspaceName = workspace.DisplayName ?? workspace.Name;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to derive workspace from selected board during SaveConfigAsync");
            }

            if (!string.IsNullOrEmpty(workspaceId) || !string.IsNullOrEmpty(workspaceName))
            {
                payload = SerializePayload(request.ApiKey, request.ApiToken, workspaceId, workspaceName);
                encryptedPayload = _dataProtector.Protect(payload);
                tempConfig.EncryptedProviderPayload = encryptedPayload;
            }

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

            await UpsertIntegrationStatusAsync(organizationId, provider, IntegrationStatus.Active, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result> SaveCredentialsAsync(
            SaveProviderCredentialsRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var payload = SerializePayload(request.ApiKey, request.ApiToken);
            var encryptedPayload = _dataProtector.Protect(payload);

            var tempConfig = new OrganizationIntegrationConfig
            {
                OrganizationId = organizationId,
                Provider = provider,
                EncryptedProviderPayload = encryptedPayload
            };

            var healthResult = await providerImpl.ValidateCredentialsAsync(tempConfig, cancellationToken);
            if (!healthResult.IsHealthy)
                return Result.Failure(new Error("Unauthorized", $"Invalid credentials: {healthResult.ErrorMessage}", 401));

            var existingConfig = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (existingConfig != null)
            {
                existingConfig.EncryptedProviderPayload = encryptedPayload;
                existingConfig.SelectedProjectId = string.Empty;
                existingConfig.SelectedListId = string.Empty;
                existingConfig.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.OrganizationIntegrationConfigs.Add(new OrganizationIntegrationConfig
                {
                    OrganizationId = organizationId,
                    Provider = provider,
                    SelectedProjectId = string.Empty,
                    SelectedListId = string.Empty,
                    EncryptedProviderPayload = encryptedPayload
                });
            }

            await UpsertIntegrationStatusAsync(organizationId, provider, IntegrationStatus.Disabled, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result> SaveDestinationAsync(
            SaveIntegrationDestinationRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (config == null || string.IsNullOrEmpty(config.EncryptedProviderPayload))
                return Result.Failure(new Error("BadRequest", "Integration credentials not configured.", 400));

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var payload = ReadPayload(config.EncryptedProviderPayload);

            var workspaces = await providerImpl.ListWorkspacesAsync(config, cancellationToken);
            var workspace = workspaces.FirstOrDefault(w => w.Id == request.WorkspaceId);
            if (workspace == null)
                return Result.Failure(new Error("BadRequest", "Workspace not found.", 400));

            var boards = await providerImpl.ListBoardsAsync(config, request.WorkspaceId, openOnly: true, cancellationToken);
            var board = boards.FirstOrDefault(b => b.Id == request.BoardId);
            if (board == null)
                return Result.Failure(new Error("BadRequest", "Board not found or is closed.", 400));

            if (!string.Equals(board.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
                return Result.Failure(new Error("BadRequest", "Board does not belong to the selected workspace.", 400));

            var lists = await providerImpl.ListListsAsync(config, request.BoardId, cancellationToken);
            if (lists.All(l => l.Id != request.ListId))
                return Result.Failure(new Error("BadRequest", "List not found on the selected board.", 400));

            config.SelectedProjectId = request.BoardId;
            config.SelectedListId = request.ListId;
            config.EncryptedProviderPayload = _dataProtector.Protect(SerializePayload(
                payload.ApiKey,
                payload.ApiToken,
                workspace.Id,
                workspace.DisplayName ?? workspace.Name));
            config.UpdatedAtUtc = DateTime.UtcNow;

            await UpsertIntegrationStatusAsync(organizationId, provider, IntegrationStatus.Active, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result<List<ProviderWorkspaceResponse>>> GetWorkspacesAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var config = await RequireConfiguredCredentialsAsync(organizationId, provider, cancellationToken);
            if (config.IsFailure)
                return Result.Failure<List<ProviderWorkspaceResponse>>(config.Error);

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var workspaces = await providerImpl.ListWorkspacesAsync(config.Value, cancellationToken);

            return Result.Success(workspaces.Select(w => new ProviderWorkspaceResponse
            {
                Id = w.Id,
                Name = w.Name,
                DisplayName = w.DisplayName,
                Url = w.Url
            }).ToList());
        }

        public async Task<Result<List<ProviderBoardResponse>>> GetBoardsAsync(
            Guid organizationId, ExternalProvider provider, string workspaceId,
            CancellationToken cancellationToken = default)
        {
            var config = await RequireConfiguredCredentialsAsync(organizationId, provider, cancellationToken);
            if (config.IsFailure)
                return Result.Failure<List<ProviderBoardResponse>>(config.Error);

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var boards = await providerImpl.ListBoardsAsync(config.Value, workspaceId, openOnly: true, cancellationToken);

            return Result.Success(boards.Select(b => new ProviderBoardResponse
            {
                Id = b.Id,
                Name = b.Name,
                Url = b.Url,
                WorkspaceId = b.WorkspaceId,
                Closed = b.Closed
            }).ToList());
        }

        public async Task<Result<List<ProviderMemberResponse>>> GetBoardMembersAsync(
            Guid organizationId, ExternalProvider provider, string boardId,
            CancellationToken cancellationToken = default)
        {
            var config = await RequireConfiguredCredentialsAsync(organizationId, provider, cancellationToken);
            if (config.IsFailure)
                return Result.Failure<List<ProviderMemberResponse>>(config.Error);

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var members = await providerImpl.ListBoardMembersAsync(config.Value, boardId, cancellationToken);

            return Result.Success(members.Select(m => new ProviderMemberResponse
            {
                Id = m.Id,
                Username = m.Username,
                FullName = m.FullName,
                AvatarUrl = m.AvatarUrl
            }).ToList());
        }

        public async Task<Result<List<ProviderProjectResponse>>> GetProjectsAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default)
        {
            var config = await RequireConfiguredCredentialsAsync(organizationId, provider, cancellationToken);
            if (config.IsFailure)
                return Result.Failure<List<ProviderProjectResponse>>(config.Error);

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var projects = await providerImpl.ListProjectsAsync(config.Value, cancellationToken);

            return Result.Success(projects.Select(p => new ProviderProjectResponse { Id = p.Id, Name = p.Name }).ToList());
        }

        public async Task<Result<List<ProviderListResponse>>> GetListsAsync(
            Guid organizationId, ExternalProvider provider, string projectId,
            CancellationToken cancellationToken = default)
        {
            var config = await RequireConfiguredCredentialsAsync(organizationId, provider, cancellationToken);
            if (config.IsFailure)
                return Result.Failure<List<ProviderListResponse>>(config.Error);

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var lists = await providerImpl.ListListsAsync(config.Value, projectId, cancellationToken);

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
            var isOrgMember = await _dbContext.UserOrgMemberships
                .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, cancellationToken);

            if (!isOrgMember)
                return Result.Failure(new Error("BadRequest", "User does not belong to this organization.", 400));

            var existing = await _dbContext.ExternalMemberMappings
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.Provider == provider, cancellationToken);

            if (string.IsNullOrWhiteSpace(request.ExternalMemberId))
            {
                if (existing != null)
                    _dbContext.ExternalMemberMappings.Remove(existing);

                await _dbContext.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }

            var integration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Type == provider, cancellationToken);

            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (integration?.Status != IntegrationStatus.Active
                || config == null
                || string.IsNullOrEmpty(config.SelectedProjectId))
            {
                return Result.Failure(new Error("BadRequest", "Integration is not active or board is not selected.", 400));
            }

            var providerImpl = _providerFactory.GetProvider(provider.ToString());
            var isValidMember = await providerImpl.ValidateAssigneeAsync(
                config,
                config.SelectedProjectId,
                request.ExternalMemberId.Trim(),
                cancellationToken);

            if (!isValidMember)
                return Result.Failure(new Error("BadRequest", "External member is not on the selected board.", 400));

            if (existing != null)
            {
                existing.ExternalMemberId = request.ExternalMemberId.Trim();
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.ExternalMemberMappings.Add(new ExternalMemberMapping
                {
                    OrganizationId = organizationId,
                    UserId = userId,
                    Provider = provider,
                    ExternalMemberId = request.ExternalMemberId.Trim()
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        private async Task<Result<OrganizationIntegrationConfig>> RequireConfiguredCredentialsAsync(
            Guid organizationId,
            ExternalProvider provider,
            CancellationToken cancellationToken)
        {
            var config = await _dbContext.OrganizationIntegrationConfigs
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider, cancellationToken);

            if (config == null || string.IsNullOrEmpty(config.EncryptedProviderPayload))
                return Result.Failure<OrganizationIntegrationConfig>(new Error("BadRequest", "Integration credentials not configured.", 400));

            return Result.Success(config);
        }

        private async Task UpsertIntegrationStatusAsync(
            Guid organizationId,
            ExternalProvider provider,
            IntegrationStatus status,
            CancellationToken cancellationToken)
        {
            var existingIntegration = await _dbContext.OrganizationIntegrations
                .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Type == provider, cancellationToken);

            if (existingIntegration != null)
            {
                existingIntegration.Status = status;
                existingIntegration.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.OrganizationIntegrations.Add(new OrganizationIntegration
                {
                    OrganizationId = organizationId,
                    Type = provider,
                    Status = status
                });
            }
        }

        private ProviderPayload ReadPayload(string encryptedPayload)
        {
            var json = _dataProtector.Unprotect(encryptedPayload);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ProviderPayload
            {
                ApiKey = root.GetProperty("apiKey").GetString() ?? string.Empty,
                ApiToken = root.GetProperty("apiToken").GetString() ?? string.Empty,
                WorkspaceId = root.TryGetProperty("workspaceId", out var workspaceId) ? workspaceId.GetString() : null,
                WorkspaceName = root.TryGetProperty("workspaceName", out var workspaceName) ? workspaceName.GetString() : null
            };
        }

        private static string SerializePayload(
            string apiKey,
            string apiToken,
            string? workspaceId = null,
            string? workspaceName = null)
        {
            var payload = new Dictionary<string, string?>
            {
                ["apiKey"] = apiKey,
                ["apiToken"] = apiToken
            };

            if (!string.IsNullOrEmpty(workspaceId))
                payload["workspaceId"] = workspaceId;

            if (!string.IsNullOrEmpty(workspaceName))
                payload["workspaceName"] = workspaceName;

            return JsonSerializer.Serialize(payload);
        }

        private sealed class ProviderPayload
        {
            public string ApiKey { get; init; } = string.Empty;
            public string ApiToken { get; init; } = string.Empty;
            public string? WorkspaceId { get; init; }
            public string? WorkspaceName { get; init; }
        }
    }
}
