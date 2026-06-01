using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Identity;

public sealed class AuthRegistrationTests(MeetingAssistantWebFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Register_WhenAutoConfirmIsEnabled_ShouldConfirmEmailAndAllowImmediateLogin()
    {
        Client.DefaultRequestHeaders.Authorization = null;
        var email = $"autoconfirm-{Guid.NewGuid():N}@test.com";
        const string password = "Password#123";

        var registerResponse = await Client.PostAsJsonAsync("/api/Auth/register", new
        {
            email,
            password,
            displayName = "Auto Confirm"
        });

        registerResponse.EnsureSuccessStatusCode();

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.EmailConfirmed.Should().BeTrue();

        var loginResponse = await Client.PostAsJsonAsync("/api/Auth/login", new
        {
            email,
            password
        });

        loginResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Register_WhenAutoConfirmIsDisabled_ShouldRequireEmailConfirmationBeforeLogin()
    {
        using var disabledFactory = new AuthConfigWebFactory(autoConfirmNewAccounts: false);
        using var client = disabledFactory.CreateClient();

        await using (var setupScope = disabledFactory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var email = $"confirm-required-{Guid.NewGuid():N}@test.com";
        const string password = "Password#123";

        var registerResponse = await client.PostAsJsonAsync("/api/Auth/register", new
        {
            email,
            password,
            displayName = "Confirm Required"
        });

        registerResponse.EnsureSuccessStatusCode();

        await using (var assertScope = disabledFactory.Services.CreateAsyncScope())
        {
            var db = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            user.EmailConfirmed.Should().BeFalse();
        }

        var loginResponse = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email,
            password
        });

        loginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var responseBody = await loginResponse.Content.ReadAsStringAsync();
        responseBody.Should().Contain("EmailNotConfirmed");
    }

    private sealed class AuthConfigWebFactory(bool autoConfirmNewAccounts) : MeetingAssistantWebFactory(
        new Dictionary<string, string?>
        {
            ["Auth:AutoConfirmNewAccounts"] = autoConfirmNewAccounts.ToString()
        });
}
