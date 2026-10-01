using BenchmarkDotNet.Running;

// No benchmark classes exist yet - Phase 1 only wires up the project so
// `dotnet run -c Release` has somewhere to go once RetailFlow.Application has
// command/query handlers worth measuring (see docs/PERFORMANCE.md).
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal partial class Program;
