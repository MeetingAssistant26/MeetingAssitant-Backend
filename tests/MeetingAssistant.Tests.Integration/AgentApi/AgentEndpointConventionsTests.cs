using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public class AgentEndpointConventionsTests
{
    [Fact]
    public void AllAgentApiEndpointMethods_ShouldAcceptCancellationTokenAsLastParameter()
    {
        // Arrange
        var assembly = typeof(MeetingAssistant.Api.Program).Assembly;

        var actionMethods = assembly.GetTypes()
            .Where(t => t.IsClass && t.Namespace != null && t.Namespace.StartsWith("MeetingAssistant.Features.AgentApi.Endpoints."))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(Task<IActionResult>))
            .ToList();

        // Act
        var violations = actionMethods
            .Where(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == 0 || parameters[^1].ParameterType != typeof(CancellationToken);
            })
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToList();

        // Assert
        violations.Should().BeEmpty("all async endpoint methods must take CancellationToken as the last parameter");
    }

    [Fact]
    public void AllAgentApiEndpointFiles_ShouldUseResultToProblemForFailurePaths()
    {
        // Arrange
        var repositoryRoot = FindRepositoryRoot();
        var endpointDir = Path.Combine(repositoryRoot, "MeetingAssistant", "Features", "AgentApi", "Endpoints");

        // Act
        var endpointFiles = Directory.GetFiles(endpointDir, "*Endpoint.cs", SearchOption.AllDirectories);
        var missingToProblem = endpointFiles
            .Where(file => !File.ReadAllText(file).Contains("ToProblem("))
            .ToList();

        // Assert
        missingToProblem.Should().BeEmpty("every endpoint file should delegate failures through Result.ToProblem()");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "MeetingAssistant.sln");
            if (File.Exists(marker))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be resolved from test runtime path.");
    }
}
