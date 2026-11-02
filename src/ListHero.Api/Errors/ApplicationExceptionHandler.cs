using ListHero.Application.Common;
using ListHero.Application.Discovery;
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
            HandleUnavailableException => StatusCodes.Status409Conflict,
            PublicProfileRequiredException => StatusCodes.Status400BadRequest,
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
                    404 => "This resource is unavailable.",
                    403 => "This action is unavailable for this account.",
                    409 when exception is HandleUnavailableException => "That handle is already taken. Choose another.",
                    409 => "This record changed. Reload it before trying again.",
                    400 when exception is PublicProfileRequiredException => "Set up your public profile before making this list public.",
                    _ => "One or more values are invalid."
                },
                Detail = status == 400 && exception is ArgumentException ? exception.Message : null,
                Extensions = { ["code"] = exception is HandleUnavailableException ? "handle_unavailable"
                    : exception is PublicProfileRequiredException ? "profile_required" : "" }
            }
        });
    }
}
