using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;

namespace Telemetrix.Transformer.Tests;

public class InvalidBatchTests
{
    [Theory]
    [InlineData("broken", "invalid-json")]
    [InlineData("version", "unsupported-schema-version")]
    [InlineData("missing", "schema-violation")]
    [InlineData("missing-value", "schema-violation")]
    [InlineData("missing-time", "schema-violation")]
    [InlineData("missing-status", "schema-violation")]
    [InlineData("null-samples", "schema-violation")]
    [InlineData("null-sample", "schema-violation")]
    [InlineData("empty-samples", "schema-violation")]
    [InlineData("null-asset", "schema-violation")]
    [InlineData("blank-tag", "schema-violation")]
    [InlineData("wrong-value", "schema-violation")]
    [InlineData("wrong-time", "schema-violation")]
    [InlineData("bad-time", "schema-violation")]
    [InlineData("tombstone", "schema-violation")]
    [InlineData("null", "schema-violation")]
    public void InvalidPayloadIsAcknowledgedByDlqBeforeCommitting(string kind, string reason)
    {
        var result = Result(kind);
        var publisher = new Publisher();
        var commits = 0;
        new RawBatchProcessor(new UnusedHandler(), publisher).Process(result, () =>
        {
            Assert.Same(result, publisher.Result);
            commits++;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, commits);
        Assert.Equal(reason, publisher.Reason);
        Assert.False(string.IsNullOrWhiteSpace(publisher.Error));
    }

    [Fact]
    public void FailedDlqPublicationNeverCommits()
    {
        var committed = false;
        var publisher = new Publisher { Acknowledge = () => throw new IOException("broker unavailable") };

        Assert.Throws<IOException>(() => new RawBatchProcessor(new UnusedHandler(), publisher).Process(
            Result("broken"), () => committed = true, TestContext.Current.CancellationToken));

        Assert.False(committed);
    }

    [Fact]
    public async Task OffsetWaitsForTheDlqAcknowledgement()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher { Acknowledge = () => { entered.SetResult(); return release.Task; } };
        var commits = 0;
        var processing = Task.Run(() => new RawBatchProcessor(new UnusedHandler(), publisher).Process(
            Result("broken"), () => Interlocked.Increment(ref commits), deadline.Token), deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, commits);
        release.SetResult();
        await processing.WaitAsync(deadline.Token);
        Assert.Equal(1, commits);
    }

    [Fact]
    public void DeadLetterHeadersPreserveOriginAndBoundTheUtf8ErrorToOneKilobyte()
    {
        var result = Result("broken");
        var timestamp = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(7));
        var message = KafkaDeadLetterPublisher.CreateInvalidMessage(result, "invalid-json", new string('界', 1000), timestamp);

        Assert.Same(result.Message.Value, message.Value);
        Assert.Equal(result.Message.Key, message.Key);
        Assert.Equal("invalid-json", Header("dlq.reason"));
        Assert.Equal(result.Topic, Header("dlq.source.topic"));
        Assert.Equal("2", Header("dlq.source.partition"));
        Assert.Equal("42", Header("dlq.source.offset"));
        Assert.Equal(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Header("dlq.timestamp"));
        var error = message.Headers.GetLastBytes("dlq.error");
        Assert.InRange(error.Length, 1, 1024);
        Assert.DoesNotContain('\uFFFD', new UTF8Encoding(false, true).GetString(error));
        string Header(string name) => Encoding.UTF8.GetString(message.Headers.GetLastBytes(name));
    }

    private static ConsumeResult<string, byte[]> Result(string kind)
    {
        var time = new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero);
        var batch = new TelemetryBatch(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000",
            "Plant01", "Line01", "Pump01", time, [new("FlowRate", 20, time, time, 0)]);
        var json = JsonNode.Parse(TelemetryJson.Serialize(batch))!;
        var sample = json["samples"]![0]!;
        switch (kind)
        {
            case "version": json["schemaVersion"] = 2; break;
            case "missing": json.AsObject().Remove("batchId"); break;
            case "missing-value": sample.AsObject().Remove("value"); break;
            case "missing-time": sample.AsObject().Remove("sourceTimestamp"); break;
            case "missing-status": sample.AsObject().Remove("statusCode"); break;
            case "null-samples": json["samples"] = null; break;
            case "null-sample": json["samples"]![0] = null; break;
            case "empty-samples": json["samples"]!.AsArray().Clear(); break;
            case "null-asset": json["assetId"] = null; break;
            case "blank-tag": sample["tag"] = " "; break;
            case "wrong-value": sample["value"] = true; break;
            case "wrong-time": sample["sourceTimestamp"] = 123; break;
            case "bad-time": sample["sourceTimestamp"] = "yesterday"; break;
        }
        return new()
        {
            Topic = KafkaOptions.RawTopic, Partition = 2, Offset = 42,
            Message = new()
            {
                Key = "original-key",
                Value = kind switch
                {
                    "broken" => "{broken"u8.ToArray(), "tombstone" => null!,
                    "null" => "null"u8.ToArray(), _ => Encoding.UTF8.GetBytes(json.ToJsonString())
                }
            }
        };
    }

    private sealed class UnusedHandler : IRawBatchHandler
    {
        public Task HandleAsync(RawBatchDelivery delivery, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("invalid batch must not reach storage");
    }

    private sealed class Publisher : IInvalidBatchPublisher
    {
        public ConsumeResult<string, byte[]>? Result { get; private set; }
        public string? Reason { get; private set; }
        public string? Error { get; private set; }
        public Func<Task> Acknowledge { get; init; } = () => Task.CompletedTask;
        public Task PublishAsync(ConsumeResult<string, byte[]> result, string reason, string error, CancellationToken cancellationToken)
        {
            Result = result;
            Reason = reason;
            Error = error;
            return Acknowledge();
        }
    }
}
