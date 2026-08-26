using RetailFlow.Shared.Correlation;
using Serilog.Context;

namespace RetailFlow.Api.Middleware;

/// <summary>
/// Reads (or mints) a correlation ID at the very edge of the request pipeline,
/// echoes it back on the response, pushes it into the ambient provider so any
/// domain event raised during this request can carry it, and into Serilog's
/// LogContext so every log line for this request is tagged with it. Must run
/// before everything else - see registration order in Program.cs.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : Guid.NewGuid().ToString("n");

        AmbientCorrelationIdProvider.Set(correlationId);
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
