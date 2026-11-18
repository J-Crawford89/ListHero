using ListHero.Hosting;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ListHero.Web.Services;

public sealed class DiagnosticCircuitHandler(ILoggerFactory loggers) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
        => async context =>
        {
            var logger = loggers.CreateLogger("ListHero.Diagnostics.Circuit");
            using var operation = AuthenticationDiagnostics.BeginOperation(logger);
            try { await next(context); }
            catch (Exception exception)
            {
                logger.Log(exception is OperationCanceledException ? LogLevel.Debug : LogLevel.Error, new EventId(2300, "CircuitActivityFailed"),
                    "Circuit activity failed {Failure}", OperationalDiagnostics.Failure(exception));
                throw;
            }
        };
}
