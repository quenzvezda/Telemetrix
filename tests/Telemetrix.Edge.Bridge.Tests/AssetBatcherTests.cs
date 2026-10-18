using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Batching;

namespace Telemetrix.Edge.Bridge.Tests;

public class AssetBatcherTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task MixedAssetsKeepTheirOwnArrivalOrderAndEnvelope()
    {
        var input = Channel.CreateUnbounded<Sample>();
        var output = Channel.CreateUnbounded<TelemetryBatch>();
        foreach (var (asset, value) in new[] { ("Pump01", 2d), ("Tank01", 3d), ("Pump01", 1d) })
        {
            input.Writer.TryWrite(Sample(asset, value));
        }
        input.Writer.Complete();

        await Batcher().RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);

        var batches = await ReadAllAsync(output.Reader);
        var pump = Assert.Single(batches, batch => batch.AssetId == "Pump01");
        Assert.Equal(new double?[] { 2, 1 }, pump.Samples.Select(sample => sample.Value));
        Assert.Single(Assert.Single(batches, batch => batch.AssetId == "Tank01").Samples);
        Assert.Equal("Plant01", pump.PlantId);
        Assert.Equal("Line01", pump.LineId);
        Assert.Equal("opcua", pump.Source);
        Assert.Equal("opc.tcp://localhost:50000", pump.Endpoint);
        Assert.Equal(time.GetUtcNow(), pump.CreatedAt);
        Assert.Equal(1, pump.SchemaVersion);
        Assert.Equal(7, pump.BatchId.Version);
        Assert.Equal(2, batches.Select(batch => batch.BatchId).Distinct().Count());
    }

    [Fact]
    public async Task SizeThresholdFlushesImmediatelyAndStartsANewBatch()
    {
        var input = Channel.CreateUnbounded<Sample>();
        var output = Channel.CreateUnbounded<TelemetryBatch>();
        for (var index = 0; index < 501; index++) input.Writer.TryWrite(Sample("Pump01", index));
        var running = Batcher().RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);

        var full = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(500, full.Samples.Count);
        Assert.Equal(499, full.Samples[^1].Value);
        input.Writer.Complete();
        await running;
        var final = Assert.Single(await ReadAllAsync(output.Reader));
        Assert.Equal(500, Assert.Single(final.Samples).Value);
        Assert.NotEqual(full.BatchId, final.BatchId);
    }

    [Fact]
    public async Task AgeFlushesFromTheFirstSampleEvenWhenNoMoreSamplesArrive()
    {
        var input = Channel.CreateUnbounded<Sample>();
        var output = Channel.CreateUnbounded<TelemetryBatch>();
        for (var index = 0; index < 35; index++) input.Writer.TryWrite(Sample("Pump01", index));
        var running = Batcher().RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(output.Reader.TryPeek(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        var batch = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(35, batch.Samples.Count);
        Assert.Equal(time.GetUtcNow(), batch.CreatedAt);
        input.Writer.Complete();
        await running;
        Assert.Empty(await ReadAllAsync(output.Reader));
    }

    [Fact]
    public async Task QuietAssetsNeverProduceEmptyBatches()
    {
        var input = Channel.CreateUnbounded<Sample>();
        var output = Channel.CreateUnbounded<TelemetryBatch>();
        var running = Batcher().RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));
        input.Writer.Complete();
        await running;

        Assert.Empty(await ReadAllAsync(output.Reader));
    }

    [Fact]
    public async Task CompletingInputFlushesEveryOpenBatch()
    {
        var input = Channel.CreateUnbounded<Sample>();
        var output = Channel.CreateUnbounded<TelemetryBatch>();
        for (var index = 0; index < 120; index++) input.Writer.TryWrite(Sample("Pump01", index));
        input.Writer.TryWrite(Sample("Tank01", null, 0x80000000));
        var running = Batcher().RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);
        Assert.False(output.Reader.TryPeek(out _));

        input.Writer.Complete();
        await running;

        var batches = await ReadAllAsync(output.Reader);
        Assert.Equal(120, Assert.Single(batches, batch => batch.AssetId == "Pump01").Samples.Count);
        var tank = Assert.Single(Assert.Single(batches, batch => batch.AssetId == "Tank01").Samples);
        Assert.Null(tank.Value);
        Assert.Equal(0x80000000u, tank.StatusCode);
        Assert.Equal(time.GetUtcNow(), tank.SourceTimestamp);
    }

    [Fact]
    public async Task BoundedOutputWaitsForThePublisherWithoutDroppingBatches()
    {
        var input = Channel.CreateUnbounded<Sample>();
        var output = Channel.CreateBounded<TelemetryBatch>(1);
        input.Writer.TryWrite(Sample("Pump01", 1));
        input.Writer.TryWrite(Sample("Pump01", 2));
        input.Writer.Complete();
        var running = Batcher(new BatchingOptions { MaxSamples = 1 })
            .RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);

        Assert.False(running.IsCompleted);
        Assert.Equal(1, (await output.Reader.ReadAsync(TestContext.Current.CancellationToken)).Samples[0].Value);
        await running;
        Assert.Equal(2, Assert.Single(await ReadAllAsync(output.Reader)).Samples[0].Value);
    }

    private AssetBatcher Batcher(BatchingOptions? options = null) =>
        new(options ?? new BatchingOptions(), time, "opcua", "opc.tcp://localhost:50000");

    private Sample Sample(string asset, double? value, uint status = 0) =>
        new("Plant01", "Line01", asset, "FlowRate", value, time.GetUtcNow(), time.GetUtcNow(), status);

    private static async Task<List<TelemetryBatch>> ReadAllAsync(ChannelReader<TelemetryBatch> reader)
    {
        var result = new List<TelemetryBatch>();
        await foreach (var batch in reader.ReadAllAsync(TestContext.Current.CancellationToken)) result.Add(batch);
        return result;
    }
}
