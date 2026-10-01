namespace RetailFlow.Shared.Results;

public enum ErrorType
{
    Failure,
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
}

/// <summary>
/// A typed, structured failure - used instead of throwing for expected business
/// rule violations (e.g. "insufficient stock") so callers can handle them without
/// paying exception overhead or writing try/catch around ordinary control flow.
/// Reserve real exceptions for things that are actually exceptional (bugs, infra
/// failures). <see cref="Type"/> is what RetailFlow.Api's exception/result-handling
/// middleware switches on to pick an HTTP status code.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
}
