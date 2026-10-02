using System.ComponentModel.DataAnnotations;

namespace ListHero.Api.Endpoints;

public sealed class RequestValidationFilter<T> : IEndpointFilter where T : class
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<T>().Single();
        var errors = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true))
            return next(context);
        var response = errors.SelectMany(error => error.MemberNames.DefaultIfEmpty(string.Empty)
            .Select(member => new { Member = member, Message = error.ErrorMessage ?? "Invalid value." }))
            .GroupBy(error => error.Member)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray());
        return ValueTask.FromResult<object?>(Results.ValidationProblem(response));
    }
}
