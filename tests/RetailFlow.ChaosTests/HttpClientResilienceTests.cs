using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using RetailFlow.Infrastructure.Resilience;

namespace RetailFlow.ChaosTests;

/// <summary>
/// Proves <see cref="HttpClientResilienceExtensions.AddRetailFlowResilience"/>
/// actually behaves as configured against a real (fake-backed) HttpClient
/// pipeline, not just that the options object is well-formed.
/// </summary>
public class HttpClientResilienceTests
{
    private sealed class AlwaysUnavailableHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task Pipeline_OpensTheCircuit_UnderSustainedFailure_AndStopsCallingTheHandler()
    {
        var handler = new AlwaysUnavailableHandler();

        var services = new ServiceCollection();
        services.AddHttpClient("external-api")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddRetailFlowResilience();

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("external-api");
        client.BaseAddress = new Uri("https://retailflow-chaos-test.invalid");

        // Drive real (failing) requests through the pipeline - each one may retry
        // internally - until the circuit breaker trips. Bounded loop so a
        // misconfigured pipeline that never opens fails the test instead of hanging.
        BrokenCircuitException? brokenCircuit = null;
        for (var attempt = 0; attempt < 10 && brokenCircuit is null; attempt++)
        {
            try
            {
                await client.GetAsync("/ping");
            }
            catch (BrokenCircuitException ex)
            {
                brokenCircuit = ex;
            }
        }

        Assert.NotNull(brokenCircuit);

        var callCountWhileOpen = handler.CallCount;

        // The circuit is open: a further call must fail immediately, without the
        // handler being invoked again.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("/ping"));

        Assert.Equal(callCountWhileOpen, handler.CallCount);
    }
}
