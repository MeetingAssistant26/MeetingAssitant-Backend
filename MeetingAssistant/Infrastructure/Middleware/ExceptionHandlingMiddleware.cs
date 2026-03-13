using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Api.Infrastructure.Services;

namespace MeetingAssistant.Infrastructure.Middleware
{
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, ICorrelationIdProvider correlationIdProvider)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                var correlationId = correlationIdProvider.CorrelationId;
                
                _logger.LogError(ex, "An unhandled exception occurred. CorrelationId: {CorrelationId}", correlationId);

                await HandleExceptionAsync(context, ex, correlationId);
            }
        }

        private static async Task HandleExceptionAsync(HttpContext context, Exception exception, string? correlationId)
        {
            context.Response.ContentType = MediaTypeNames.Application.Json;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var response = new StandardErrorResponse
            {
                Type = "InternalServerError",
                Title = "An unexpected error occurred.",
                Status = StatusCodes.Status500InternalServerError,
                CorrelationId = correlationId,
                Errors = new Dictionary<string, string[]>
                {
                    { "General", new[] { "An unexpected error occurred. Please try again later." } }
                }
            };

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var json = JsonSerializer.Serialize(response, options);

            await context.Response.WriteAsync(json);
        }
    }
}
