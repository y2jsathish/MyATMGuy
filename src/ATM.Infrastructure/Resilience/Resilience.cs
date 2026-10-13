using ATM.Application.Events;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace ATM.Infrastructure.Resilience;

public sealed class ResilienceOptions
{
    public int RetryAttempts { get; set; } = 3;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 4;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Named Polly v8 pipelines: timeout, retry with jitter, circuit breaker. Breaker transitions are logged and published as events
/// so health checks and operators see them.
/// </summary>
public sealed class ResiliencePipelines(IEventBus bus, ILogger<ResiliencePipelines> logger, ResilienceOptions options)
{
    public const string Monitoring = "monitoring";

    private readonly ResiliencePipelineRegistry<string> registry = new();

    public ResiliencePipeline Get(string name) =>
        registry.GetOrAddPipeline(name, builder =>
            builder
                .AddTimeout(new TimeoutStrategyOptions { Timeout = options.Timeout })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = Math.Max(1, options.RetryAttempts),
                    Delay = options.RetryBaseDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TimeoutRejectedException>().Handle<IOException>(),
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = options.FailureRatio,
                    MinimumThroughput = options.MinimumThroughput,
                    SamplingDuration = options.SamplingDuration,
                    BreakDuration = options.BreakDuration < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : options.BreakDuration,
                    ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TimeoutRejectedException>().Handle<IOException>(),
                    OnOpened = args => Changed(name, "Open", args.BreakDuration),
                    OnClosed = _ => Changed(name, "Closed", null),
                    OnHalfOpened = _ => Changed(name, "HalfOpen", null),
                }));

    private ValueTask Changed(string name, string state, TimeSpan? breakDuration)
    {
        logger.LogWarning("Circuit breaker {Pipeline} is now {State} {BreakDuration}", name, state, breakDuration);
        return bus.PublishAsync(new CircuitBreakerStateChanged(name, state));
    }
}
