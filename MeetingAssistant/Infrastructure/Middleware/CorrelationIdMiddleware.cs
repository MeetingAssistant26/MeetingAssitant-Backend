using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Serilog.Context;
using MeetingAssistant.Api.Infrastructure.Services;

namespace MeetingAssistant.Infrastructure.Middleware
{
    public class CorrelationIdMiddleware
    {
        private const string CorrelationIdHeaderName = "X-Correlation-Id";
        private readonly RequestDelegate _next;

        public CorrelationIdMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ICorrelationIdProvider correlationIdProvider)
        {
            var correlationId = context.Request.Headers[CorrelationIdHeaderName].FirstOrDefault() ?? Guid.NewGuid().ToString();

            correlationIdProvider.CorrelationId = correlationId;

            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey(CorrelationIdHeaderName))
                {
                    context.Response.Headers[CorrelationIdHeaderName] = correlationId;
                }
                return Task.CompletedTask;
            });

            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                await _next(context);
            }
        }
    }
}
