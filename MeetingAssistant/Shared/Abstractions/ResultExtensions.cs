using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Api.Shared;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Text.Json;

namespace MeetingAssistant.Shared.Abstractions
{
    public static class ResultExtensions
    {
        public static ObjectResult ToProblem(this Result result,ICorrelationIdProvider correlationProvider)
        {
            if (result.IsSuccess)
            {
                throw new InvalidOperationException("Cannot convert a successful result to a problem.");
            }

            var response = new StandardErrorResponse
            {
                Type = result.Error.Code,
                Title = result.Error.Description,
                Status = result.Error.Statuscode,
                Errors = result.Error.Errors,
                CorrelationId = correlationProvider.CorrelationId
            };

            if (result.Error.Extensions is { Count: > 0 })
            {
                response.Extensions = result.Error.Extensions.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.SerializeToElement(
                        pair.Value,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            
            return new ObjectResult(response)
            {
                StatusCode = result.Error.Statuscode
            };
        }
    }
}
