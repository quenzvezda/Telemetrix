using System.Threading.Channels;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge;

public sealed class OpcUaIngestionWorker(
    ISourceAdapter source,
    ILogger<OpcUaIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateBounded<Sample>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var collecting = CollectAsync(channel.Writer, sourceCancellation.Token);
        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(stoppingToken))
            {
                logger.LogInformation(
                    "Sample {Plant}/{Line}/{Asset}/{Tag}: value={Value}, source={SourceTimestamp:O}, server={ServerTimestamp:O}, status=0x{StatusCode:X8}",
                    sample.Plant, sample.Line, sample.Asset, sample.Tag, sample.Value,
                    sample.SourceTimestamp, sample.ServerTimestamp, sample.StatusCode);
            }

            await collecting;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await sourceCancellation.CancelAsync();
            try
            {
                await collecting;
            }
            catch (OperationCanceledException) when (sourceCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task CollectAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await source.RunAsync(writer, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }
}
