using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Telemetrix.Integration.Tests;

public class ConsumerGroupReadinessTests
{
    [Theory]
    [InlineData(ErrorCode.GroupLoadInProgress)]
    [InlineData(ErrorCode.Local_TimedOut)]
    public async Task CoordinatorStartupErrorsRemainPendingUntilCancellation(ErrorCode code)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var attempts = 0;
        var failure = Failure(code);
        Task<DescribeConsumerGroupsResult> Describe()
        {
            if (++attempts == 2) cancellation.Cancel();
            return Task.FromException<DescribeConsumerGroupsResult>(failure);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConsumerGroupReadiness.WaitForMembersAsync(Describe, 2, cancellation.Token));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AuthorizationErrorsFailImmediately()
    {
        var failure = Failure(ErrorCode.GroupAuthorizationFailed);
        Assert.Same(failure, await Assert.ThrowsAsync<DescribeConsumerGroupsException>(() =>
            ConsumerGroupReadiness.WaitForMembersAsync(() => Task.FromException<DescribeConsumerGroupsResult>(failure),
                2, TestContext.Current.CancellationToken)));
    }

    private static DescribeConsumerGroupsException Failure(ErrorCode code) => new(new DescribeConsumerGroupsReport
    {
        ConsumerGroupDescriptions = [new ConsumerGroupDescription { GroupId = "transformer", Error = new Error(code) }]
    });
}
