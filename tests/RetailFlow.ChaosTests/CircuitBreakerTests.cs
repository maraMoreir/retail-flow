using Polly;
using Polly.CircuitBreaker;

namespace RetailFlow.ChaosTests;

/// <summary>
/// Demonstrates the circuit-breaker pattern RetailFlow.Infrastructure applies to
/// outbound calls (e.g. the Fiscal authorization endpoint in later phases) using
/// the modern Polly v8 ResiliencePipeline API - not the legacy Policy.Handle&lt;T&gt;()
/// syntax, which no longer applies to Polly 8+.
/// </summary>
public class CircuitBreakerTests
{
    private sealed class SimulatedDownstreamFailure : Exception;

    [Fact]
    public async Task Pipeline_OpensTheCircuit_AfterEnoughFailures_AndStopsCallingTheDelegate()
    {
        var invocationCount = 0;

        var pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<SimulatedDownstreamFailure>(),
            })
            .Build();

        async ValueTask AlwaysFailsAsync(CancellationToken ct)
        {
            invocationCount++;
            await Task.Yield();
            throw new SimulatedDownstreamFailure();
        }

        // Feed it enough failures to trip the breaker (MinimumThroughput = 4).
        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<SimulatedDownstreamFailure>(
                async () => await pipeline.ExecuteAsync(ct => AlwaysFailsAsync(ct)));
        }

        var invocationsBeforeOpenCircuitCall = invocationCount;

        // The circuit should now be open: the delegate must NOT run at all.
        await Assert.ThrowsAsync<BrokenCircuitException>(
            async () => await pipeline.ExecuteAsync(ct => AlwaysFailsAsync(ct)));

        Assert.Equal(invocationsBeforeOpenCircuitCall, invocationCount);
    }
}
