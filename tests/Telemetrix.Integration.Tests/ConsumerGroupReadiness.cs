using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Telemetrix.Transformer.Consuming;

namespace Telemetrix.Integration.Tests;

internal static class ConsumerGroupReadiness
{
    public static Task<ConsumerGroupDescription> WaitForMembersAsync(IAdminClient admin, int count, CancellationToken ct) =>
        WaitForMembersAsync(() => admin.DescribeConsumerGroupsAsync([KafkaOptions.ConsumerGroup],
            new DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }), count, ct);

    internal static async Task<ConsumerGroupDescription> WaitForMembersAsync(
        Func<Task<DescribeConsumerGroupsResult>> describe, int count, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await describe().WaitAsync(ct);
                var group = Assert.Single(result.ConsumerGroupDescriptions);
                if (group.State == ConsumerGroupState.Stable && group.Members.Count == count
                    && group.Members.Sum(member => member.Assignment.TopicPartitions.Count) == 6) return group;
            }
            catch (DescribeConsumerGroupsException exception)
            {
                var codes = exception.Results.ConsumerGroupDescriptions.Select(group => group.Error.Code).ToArray();
                Console.WriteLine("Consumer group readiness: {0}", string.Join(", ", codes));
                if (codes.Length == 0 || !codes.All(code => code is ErrorCode.GroupIdNotFound
                    or ErrorCode.GroupLoadInProgress or ErrorCode.GroupCoordinatorNotAvailable or ErrorCode.NotCoordinatorForGroup))
                    throw;
            }
            await Task.Delay(100, ct);
        }
    }
}
