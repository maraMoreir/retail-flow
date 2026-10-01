using System.Reflection;
using NetArchTest.Rules;

namespace RetailFlow.ArchitectureTests;

/// <summary>
/// Encodes the Clean Architecture / bounded-context rules described in
/// docs/ARCHITECTURE.md as executable checks, so a dependency that violates them
/// fails the build instead of getting caught (or missed) in review.
/// </summary>
public class LayerDependencyTests
{
    private static readonly Assembly Domain = Assembly.Load("RetailFlow.Domain");
    private static readonly Assembly Application = Assembly.Load("RetailFlow.Application");
    private static readonly Assembly Infrastructure = Assembly.Load("RetailFlow.Infrastructure");
    private static readonly Assembly Reporting = Assembly.Load("RetailFlow.Reporting");

    [Fact]
    public void Domain_ShouldNotDependOnAnyOtherLayerOrFramework()
    {
        var result = Types.InAssembly(Domain)
            .Should()
            .NotHaveDependencyOnAny(
                "RetailFlow.Application", "RetailFlow.Infrastructure", "RetailFlow.Reporting",
                "RetailFlow.Shared", "RetailFlow.Api", "RetailFlow.Worker",
                "Microsoft.EntityFrameworkCore", "Wolverine")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_ShouldNotDependOnInfrastructureOrHosts()
    {
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny(
                "RetailFlow.Infrastructure", "RetailFlow.Reporting", "RetailFlow.Api", "RetailFlow.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_ShouldNotDependDirectlyOnWolverine()
    {
        // Handlers are plain classes Wolverine discovers by naming convention -
        // Application stays messaging-framework-agnostic. Only RetailFlow.Infrastructure
        // (the composition-root wiring) references Wolverine directly.
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny("Wolverine")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Reporting_ShouldNotDependOnDomainOrApplication()
    {
        // CQRS read side: queries its own denormalized projections, never the
        // write-side aggregates or use cases.
        var result = Types.InAssembly(Reporting)
            .Should()
            .NotHaveDependencyOnAny("RetailFlow.Domain", "RetailFlow.Application")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Infrastructure_ShouldNotDependOnHostProjects()
    {
        // Infrastructure is consumed BY the hosts (Api, Worker); it must never
        // reference either one back, or DI composition becomes circular.
        var result = Types.InAssembly(Infrastructure)
            .Should()
            .NotHaveDependencyOnAny("RetailFlow.Api", "RetailFlow.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        "Offending types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
