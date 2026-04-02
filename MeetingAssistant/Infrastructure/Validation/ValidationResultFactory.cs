using FluentValidation;
using FluentValidation.Results;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Api.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SharpGrip.FluentValidation.AutoValidation.Mvc.Results;

namespace MeetingAssistant.Infrastructure.Validation
{
    public class ValidationResultFactory : IFluentValidationAutoValidationResultFactory
    {
        public Task<IActionResult?> CreateActionResult(ActionExecutingContext context, ValidationProblemDetails validationProblemDetails, IDictionary<IValidationContext, ValidationResult> validationResults)
        {
            var correlationIdProvider = context.HttpContext.RequestServices.GetRequiredService<ICorrelationIdProvider>();

            var response = new StandardErrorResponse
            {
                Type = "ValidationError",
                Title = "One or more validation errors occurred.",
                Status = StatusCodes.Status400BadRequest,
                Errors = new Dictionary<string, string[]>(validationProblemDetails.Errors),
                CorrelationId = correlationIdProvider.CorrelationId
            };

            return Task.FromResult<IActionResult?>(new BadRequestObjectResult(response));
        }
    }
}
