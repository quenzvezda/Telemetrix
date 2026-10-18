using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

public sealed class OpcUaSourceAdapter(
    IOptions<OpcUaOptions> options,
    ITelemetryContext telemetry,
    TimeProvider timeProvider,
    ILogger<OpcUaSourceAdapter> logger) : ISourceAdapter
{
    public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var pkiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telemetrix", "pki");
        var configuration = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = "Telemetrix.Edge.Bridge",
            ApplicationUri = $"urn:{Environment.MachineName}:Telemetrix.Edge.Bridge",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates =
                [
                    new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = Path.Combine(pkiPath, "own"),
                        SubjectName = "CN=Telemetrix.Edge.Bridge"
                    }
                ],
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiPath, "trusted")
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiPath, "issuers")
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiPath, "rejected")
                },
                AutoAcceptUntrustedCertificates = true
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken);
        var description = await CoreClientUtils.SelectEndpointAsync(
            configuration, settings.Endpoint, false, telemetry, cancellationToken)
            ?? throw new InvalidOperationException("The server offers no OPC UA endpoint with security None.");
        if (description.SecurityMode != MessageSecurityMode.None)
        {
            throw new InvalidOperationException("M2 requires an OPC UA endpoint with security None.");
        }

        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration));
        using var session = await new DefaultSessionFactory(telemetry).CreateAsync(
            configuration, endpoint, false, false, configuration.ApplicationName, 60000,
            new UserIdentity(), default, cancellationToken);
        using var subscription = new Subscription(telemetry, new SubscriptionOptions
        {
            DisplayName = "Telemetrix samples",
            PublishingInterval = settings.PublishingIntervalMs,
            KeepAliveCount = 10,
            LifetimeCount = 60,
            PublishingEnabled = true,
            SequentialPublishing = true,
            TimestampsToReturn = TimestampsToReturn.Both
        });
        var failedNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new List<(MonitoredItem Item, OpcUaTagOptions Tag, MonitoredItemNotificationEventHandler Handler)>();
        try
        {
            foreach (var tag in settings.Tags)
            {
                var item = new MonitoredItem(telemetry, new MonitoredItemOptions
                {
                    DisplayName = $"{tag.Asset}/{tag.Tag}",
                    StartNodeId = NodeId.Parse(tag.NodeId),
                    AttributeId = Attributes.Value,
                    SamplingInterval = settings.SamplingIntervalMs,
                    QueueSize = settings.QueueSize,
                    DiscardOldest = true,
                    MonitoringMode = MonitoringMode.Reporting
                });
                MonitoredItemNotificationEventHandler handler = (monitoredItem, _) =>
                {
                    try
                    {
                        foreach (var notification in monitoredItem.DequeueValues())
                        {
                            var sample = OpcUaSampleMapper.Map(settings, tag, notification);
                            // The SDK callback is synchronous. Waiting here applies channel back-pressure
                            // without async-void callbacks or untracked writes that could lose samples.
                            writer.WriteAsync(sample, cancellationToken).AsTask().GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        failedNotification.TrySetException(exception);
                    }
                };
                item.Notification += handler;
                handlers.Add((item, tag, handler));
                subscription.AddItem(item);
            }

            session.AddSubscription(subscription);
            await subscription.CreateAsync(cancellationToken);
            foreach (var (item, tag, _) in handlers)
            {
                var error = item.Status.Error;
                if (error is not null && ServiceResult.IsBad(error))
                {
                    logger.LogWarning(
                        "OPC UA monitored item failed for {Asset}/{Tag} at {NodeId}: {StatusCode} (0x{RawStatusCode:X8})",
                        tag.Asset, tag.Tag, tag.NodeId, error.StatusCode, error.StatusCode.Code);
                }
                else
                {
                    logger.LogInformation(
                        "OPC UA monitored item created for {Asset}/{Tag} at {NodeId}; sampling {SamplingIntervalMs} ms, queue {QueueSize}",
                        tag.Asset, tag.Tag, tag.NodeId, item.Status.SamplingInterval, item.Status.QueueSize);
                }
            }
            logger.LogInformation(
                "OPC UA subscription created at {Endpoint} with {RequestedTagCount} requested tags; publishing {PublishingIntervalMs} ms",
                settings.Endpoint, subscription.MonitoredItemCount, subscription.CurrentPublishingInterval);

            var stopped = Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
            await await Task.WhenAny(stopped, failedNotification.Task);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var (item, _, handler) in handlers)
            {
                item.Notification -= handler;
            }

            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
            await session.CloseAsync(closeTimeout.Token);
        }
    }
}
