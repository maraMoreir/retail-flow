using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace RetailFlow.Application;

public static class DependencyInjection
{
    public static readonly Assembly ApplicationAssembly = typeof(DependencyInjection).Assembly;

    /// <summary>
    /// Entry point for Application-layer service registrations that aren't already
    /// handled elsewhere. Deliberately does NOT register FluentValidation validators
    /// here - Wolverine's <c>opts.UseFluentValidation()</c> (configured once in
    /// RetailFlow.Infrastructure's AddRetailFlowMessaging) type-scans this same
    /// assembly and owns that registration, so a second registration here would
    /// double-run every validator.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
