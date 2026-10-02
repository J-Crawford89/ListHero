using ListHero.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace ListHero.Api.Errors;

public sealed class ApplicationExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            ResourceNotFoundException => StatusCodes.Status404NotFound,
            EditConflictException => StatusCodes.Status409Conflict,
            AccountUnavailableException or UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            ArgumentException => StatusCodes.Status400BadRequest,
            _ => 0
        };
        if (status == 0) return false;
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new()
        {
            HttpContext = context,
            ProblemDetails = new()
            {
                Status = status,
                Title = status switch
                {
                    404 => "List not found.",
                    403 => "This action is unavailable for this account.",
                    409 => "This record changed. Reload it before trying again.",
                    _ => "One or more values are invalid."
                },
                Detail = status == 400 ? exception.Message : null
            }
        });
    }
}
