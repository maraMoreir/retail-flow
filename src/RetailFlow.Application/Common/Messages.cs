namespace RetailFlow.Application.Common;

// Wolverine dispatches by naming convention (a class named e.g. "CreateSaleHandler"
// with a public "Handle"/"HandleAsync" method) - it does NOT require handlers or
// messages to implement any interface. These markers exist purely so a command vs.
// a query vs. a domain-event-reaction is obvious at a glance when reading
// Application code, and so `dotnet new` / analyzers can find "all commands" easily.
// They carry no behavior and are never referenced by Wolverine itself.

/// <summary>A request that changes state and returns nothing.</summary>
public interface ICommand;

/// <summary>A request that changes state and returns <typeparamref name="TResponse"/>.</summary>
public interface ICommand<TResponse>;

/// <summary>A request that reads state and returns <typeparamref name="TResponse"/>, changing nothing.</summary>
public interface IQuery<TResponse>;
