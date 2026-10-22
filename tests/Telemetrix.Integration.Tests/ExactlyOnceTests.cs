using Dapper;

namespace Telemetrix.Integration.Tests;

public class ExactlyOnceTests
{
    [Fact]
    public async Task ReplayingABatchStoresEveryDeliveredSampleOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        using var host = environment.Host();
        var batches = Enumerable.Range(0, 60).Select(i => DeliveryTestEnvironment.Batch(i)).ToArray();
        try
        {
            await host.StartAsync(ct);
            foreach (var batch in batches)
            {
                await environment.ProduceAsync(batch, 0, ct);
                if (batch == batches[29]) await environment.ProduceAsync(batch, 0, ct);
            }
            await environment.WaitForOffsetsAsync([61], ct);

            Assert.Equal(600, await environment.CountAsync(ct));
            await using var connection = await environment.Source.OpenConnectionAsync(ct);
            var counts = (await connection.QueryAsync<(Guid BatchId, int Count)>(new CommandDefinition(
                "SELECT batch_id, count(*)::int FROM measurement GROUP BY batch_id", cancellationToken: ct)))
                .ToDictionary(row => row.BatchId, row => row.Count);
            Assert.Equal(60, counts.Count);
            Assert.All(batches, batch => Assert.Equal(batch.Samples.Count, counts[batch.BatchId]));
            Assert.Equal(600, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(DISTINCT (tag_id, time)) FROM measurement", cancellationToken: ct)));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(cleanup.Token);
        }
    }
}
