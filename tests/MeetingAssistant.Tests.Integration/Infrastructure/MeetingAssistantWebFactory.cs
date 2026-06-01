using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MeetingAssistant.Features.AgentApi.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace MeetingAssistant.Tests.Integration.Infrastructure;

public class MeetingAssistantWebFactory : WebApplicationFactory<MeetingAssistant.Api.Program>
{
    private SqliteConnection? _connection;
    private readonly IReadOnlyDictionary<string, string?> _configurationOverrides;

    public MeetingAssistantWebFactory()
        : this(new Dictionary<string, string?>())
    {
    }

    protected MeetingAssistantWebFactory(IReadOnlyDictionary<string, string?> configurationOverrides)
    {
        _configurationOverrides = configurationOverrides;
    }

    public MeetingAssistantWebFactory WithConfiguration(IReadOnlyDictionary<string, string?> configurationOverrides)
        => new(configurationOverrides);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = TestJwtTokenHelper.TestSigningKey,
                ["Jwt:Issuer"] = TestJwtTokenHelper.TestIssuer,
                ["Jwt:Audience"] = TestJwtTokenHelper.TestAudience,
                ["Jwt:TokenExpiryMinutes"] = "60",
                ["Jwt:RefreshTokenExpiryDays"] = "14",
                ["AgentJwt:SigningKey"] = TestJwtTokenHelper.TestAgentSigningKey,
                ["AgentJwt:Issuer"] = TestJwtTokenHelper.TestAgentIssuer,
                ["AgentJwt:Audience"] = TestJwtTokenHelper.TestAgentAudience,
                ["AgentJwt:TokenExpiryMinutes"] = "60",
                ["ConnectionStrings:DefaultConnection"] = "not-used",
                ["Redis:ConnectionString"] = "not-used",
                ["HangfireSettings:DashboardUsername"] = "test",
                ["HangfireSettings:DashboardPassword"] = "test",
                ["LiveKit:ApiKey"] = "test-livekit-key",
                ["LiveKit:ApiSecret"] = "0123456789abcdef0123456789abcdef",
                ["LiveKit:ServerUrl"] = "wss://test.livekit.local",
                ["LiveKit:WebhookSecret"] = "0123456789abcdef0123456789abcdef",
                ["OpenAiCompatible:Stt:BaseUrl"] = "http://stt.test/v1",
                ["OpenAiCompatible:Stt:ApiKey"] = "test-stt-key",
                ["OpenAiCompatible:Stt:Model"] = "whisper-1",
                ["OpenAiCompatible:Llm:BaseUrl"] = "http://llm.test/v1",
                ["OpenAiCompatible:Llm:ApiKey"] = "test-llm-key",
                ["OpenAiCompatible:Llm:Model"] = "local",
                ["OpenAiCompatible:Embedding:Provider"] = "deterministic-test",
                ["OpenAiCompatible:Embedding:BaseUrl"] = "",
                ["OpenAiCompatible:Embedding:ApiKey"] = "test-embedding-key",
                ["OpenAiCompatible:Embedding:Model"] = "deterministic-test-v1",
                ["OpenAiCompatible:Embedding:Dimension"] = "1536"
            };

            foreach (var setting in _configurationOverrides)
                settings[setting.Key] = setting.Value;

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove ALL EF Core registrations (DbContext, options, and internal provider services)
            var efDescriptors = services
                .Where(d =>
                    d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true ||
                    d.ServiceType.FullName?.Contains("DbContextOptions") == true ||
                    d.ImplementationType?.FullName?.Contains("Npgsql") == true ||
                    d.ServiceType == typeof(ApplicationDbContext))
                .ToList();

            foreach (var descriptor in efDescriptors)
                services.Remove(descriptor);

            // Keep SQLite connection open for the lifetime of the factory
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<ApplicationDbContext>((sp, options) =>
            {
                options.UseSqlite(_connection);
            });

            // Remove Hangfire services that require PostgreSQL
            RemoveHangfireServices(services);
            services.AddSingleton<IBackgroundJobClient, NoOpBackgroundJobClient>();

            // Remove health checks that require real infrastructure
            services.RemoveAll<Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck>();

            // Remove Redis distributed cache, replace with in-memory
            services.RemoveAll<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
            services.AddDistributedMemoryCache();

            // Override user JWT bearer to use our test key
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        ValidateAudience = true,
                        ValidateIssuer = true,
                        ValidateLifetime = true,
                        ValidIssuer = TestJwtTokenHelper.TestIssuer,
                        ValidAudience = TestJwtTokenHelper.TestAudience,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(TestJwtTokenHelper.TestSigningKey)),
                        ClockSkew = TimeSpan.Zero
                    };
                });

            // Override agent JWT bearer to use our test key
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                AgentAuthenticationDefaults.Scheme,
                options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        ValidateAudience = true,
                        ValidateIssuer = true,
                        ValidateLifetime = true,
                        ValidIssuer = TestJwtTokenHelper.TestAgentIssuer,
                        ValidAudience = TestJwtTokenHelper.TestAgentAudience,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(TestJwtTokenHelper.TestAgentSigningKey)),
                        ClockSkew = TimeSpan.Zero
                    };
                });
        });
    }

    private static void RemoveHangfireServices(IServiceCollection services)
    {
        var hangfireDescriptors = services
            .Where(d =>
                d.ServiceType.FullName?.Contains("Hangfire") == true ||
                d.ImplementationType?.FullName?.Contains("Hangfire") == true ||
                d.ImplementationFactory?.Target?.GetType().FullName?.Contains("Hangfire") == true ||
                d.ImplementationFactory?.Method.DeclaringType?.FullName?.Contains("Hangfire") == true)
            .ToList();

        foreach (var descriptor in hangfireDescriptors)
            services.Remove(descriptor);
    }

    private sealed class NoOpBackgroundJobClient : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => Guid.NewGuid().ToString("N");

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection?.Dispose();
        }
    }
}
