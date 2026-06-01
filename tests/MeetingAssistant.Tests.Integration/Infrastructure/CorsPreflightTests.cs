using System.Net;
using FluentAssertions;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Infrastructure;

public class CorsPreflightTests : IClassFixture<MeetingAssistantWebFactory>
{
    private readonly MeetingAssistantWebFactory _factory;

    public CorsPreflightTests(MeetingAssistantWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OptionsPreflight_ToAuthenticatedJsonRoute_ShouldSucceedBeforeAuthAndRateLimiting()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/Auth/login");
        request.Headers.Add("Origin", "http://localhost:3005");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type, authorization");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).Should().BeTrue();
        origins.Should().Contain("*");
        response.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods).Should().BeTrue();
        methods!.Should().Contain(method => method.Contains("POST", StringComparison.OrdinalIgnoreCase));
        response.Headers.TryGetValues("Access-Control-Allow-Headers", out var headers).Should().BeTrue();
        headers!.Should().Contain(header => header.Contains("content-type", StringComparison.OrdinalIgnoreCase));
    }
}
