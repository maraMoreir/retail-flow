using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace RetailFlow.Infrastructure.Resilience;

/// <summary>
/// Named resilience pipeline for outbound HTTP calls to external systems (the
/// Fiscal authority, a payment gateway, etc.) - "External APIs: 50% failure
/// threshold, 30s timeout" from the project plan. Nothing calls
/// <see cref="AddRetailFlowResilience"/> yet, because no such HTTP client exists
/// yet (no Fiscal integration has been built - see docs/ARCHITECTURE.md); this is
/// the policy ready for whichever bounded context needs its first outbound
/// HttpClient. See <c>RetailFlow.ChaosTests</c> for proof this pipeline actually
/// opens the circuit under sustained failure, not just that it's configured.
/// </summary>
public static class HttpClientResilienceExtensions
{
    public const string PipelineName = "retailflow-external-api";

    public static IHttpClientBuilder AddRetailFlowResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler(PipelineName, pipeline =>
        {
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            });

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 8,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
            });

            pipeline.AddTimeout(TimeSpan.FromSeconds(30));
        });

        // AddResilienceHandler returns IHttpResiliencePipelineBuilder, not
        // IHttpClientBuilder - return the original so callers can keep chaining
        // other IHttpClientBuilder configuration (BaseAddress, DefaultHeaders, ...).
        return builder;
    }
}
