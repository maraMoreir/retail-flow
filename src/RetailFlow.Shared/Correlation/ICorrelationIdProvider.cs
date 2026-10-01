namespace RetailFlow.Shared.Correlation;

/// <summary>
/// Gives every layer a way to read the current request/message's correlation ID
/// without depending on ASP.NET Core's HttpContext or Wolverine's envelope type.
/// RetailFlow.Api resolves it from an incoming header (or mints one); RetailFlow.Worker
/// resolves it from the Wolverine envelope; both flow it into Serilog's LogContext
/// and onto every outgoing domain event, so one ID ties a request to every message
/// and log line it produced.
/// </summary>
public interface ICorrelationIdProvider
{
    string CorrelationId { get; }
}

/// <summary>
/// AsyncLocal-backed default so code that doesn't have access to HttpContext or a
/// Wolverine envelope (e.g. deep in a domain service) can still read the ambient
/// correlation ID. Hosts are responsible for calling <see cref="Set"/> once per
/// request/message at the edge.
/// </summary>
public sealed class AmbientCorrelationIdProvider : ICorrelationIdProvider
{
    private static readonly AsyncLocal<string?> Current = new();

    public string CorrelationId => Current.Value ?? "unset";

    public static void Set(string correlationId) => Current.Value = correlationId;
}
