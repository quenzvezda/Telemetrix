using Polly;
using Polly.Retry;

namespace Telemetrix.Transformer.Storage;

public static class StorageRetry
{
    public static ResiliencePipeline Create(TimeProvider timeProvider) =>
        new ResiliencePipelineBuilder { TimeProvider = timeProvider }.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(exception => exception is not OperationCanceledException),
            MaxRetryAttempts = int.MaxValue,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        }).Build();
}
