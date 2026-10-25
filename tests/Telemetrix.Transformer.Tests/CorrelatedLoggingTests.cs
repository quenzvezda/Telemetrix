using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Tests;

[CollectionDefinition("Console logging", DisableParallelization = true)]
public class ConsoleLoggingCollection;

[Collection("Console logging")]
public class CorrelatedLoggingTests
{
    [Fact]
    public async Task BatchScopeReachesNestedWriterAndSerializedJson()
    {
        var output = new StringWriter();
        var previous = Console.Out;
        var capture = new Capture();
        var batchId = Guid.NewGuid();
        try
        {
            Console.SetOut(output);
            await using var app = TransformerApplication.Build(["--Http:Port=0"]);
            var factory = app.Services.GetRequiredService<ILoggerFactory>();
            factory.AddProvider(capture);
            using var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
            var handler = new TelemetryBatchHandler(resolver, new Writer(factory.CreateLogger<Writer>()), new DeadLetter(),
                factory.CreateLogger<TelemetryBatchHandler>(), ResiliencePipeline.Empty);
            var now = DateTimeOffset.UtcNow;
            var batch = new TelemetryBatch(1, batchId, "mqtt", "mqtt://test", "Plant01", "Line01", "Pump01", now,
                [new("FlowRate", 50, now, now, 0)]);
            await handler.HandleAsync(new RawBatchDelivery(batch, "Pump01", 0, 0), TestContext.Current.CancellationToken);
        }
        finally { Console.SetOut(previous); }
        Assert.Contains(capture.Scopes, values => values.Any(pair => pair.Key == "batchId" && Equals(pair.Value, batchId)));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        var nested = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var entry = Assert.Single(nested, json => json.RootElement.GetProperty("Message").GetString() == "Nested measurement write");
            Assert.Contains(entry.RootElement.GetProperty("Scopes").EnumerateArray(), scope =>
                scope.TryGetProperty("batchId", out var id) && id.GetGuid() == batchId &&
                scope.GetProperty("assetId").GetString() == "Pump01");
        }
        finally { foreach (var json in nested) json.Dispose(); }
    }

    private sealed class Writer(ILogger<Writer> logger) : IMeasurementWriter
    {
        public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            await Task.Yield();
            logger.LogInformation("Nested measurement write");
            return samples.Count;
        }
    }

    private sealed class Catalog : ITagCatalog
    {
        public Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagMetadata>>([new(1, "Pump01", "FlowRate", "m3/h", 0, 80)]);
        public Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken) => Task.FromResult<TagMetadata?>(null);
        public Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class DeadLetter : IDeadLetterPublisher
    {
        public Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider provider = new LoggerExternalScopeProvider();
        public ConcurrentBag<IReadOnlyList<KeyValuePair<string, object?>>> Scopes { get; } = [];
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => provider = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new ScopeLogger(this);
        public void Dispose() { }
        private sealed class ScopeLogger(Capture owner) : ILogger
        {
            public bool IsEnabled(LogLevel level) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.provider.Push(state);
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.provider.ForEachScope((scope, scopes) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values) scopes.Add(values.ToArray());
                }, owner.Scopes);
        }
    }
}
