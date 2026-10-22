using Opc.Ua;
using Opc.Ua.Client;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

internal sealed class OpcUaSessionRecovery : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SessionReconnectHandler reconnect;
    private ISession session;
    private bool stopping;
    private readonly Action<string>? interrupted;
    private readonly Action? restored;

    public OpcUaSessionRecovery(ISession session, ITelemetryContext telemetry,
        TimeProvider timeProvider, ILogger logger, Action<string>? interrupted = null, Action? restored = null)
    {
        this.session = session;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.interrupted = interrupted;
        this.restored = restored;
        reconnect = new SessionReconnectHandler(telemetry, true, 15000, timeProvider);
        Attach(session);
    }

    public Task Failure => failure.Task;

    private void Attach(ISession active)
    {
        active.KeepAliveInterval = 5000;
        active.TransferSubscriptionsOnReconnect = true;
        // A recreated SDK session can carry copied handlers. Keep exactly one registration.
        active.KeepAlive -= OnKeepAlive;
        active.KeepAlive += OnKeepAlive;
    }

    private void OnKeepAlive(ISession sender, KeepAliveEventArgs args)
    {
        try
        {
            lock (gate)
            {
                if (stopping || !ReferenceEquals(sender, session) || !ServiceResult.IsBad(args.Status))
                {
                    return;
                }

                var state = reconnect.BeginReconnect(session, 1000, OnReconnected);
                if (state == SessionReconnectHandler.ReconnectState.Triggered)
                {
                    logger.LogWarning("OPC UA session interrupted with {StatusCode}; retrying connection", args.Status);
                    interrupted?.Invoke(args.Status?.ToString() ?? "keepalive-failed");
                }

                args.CancelKeepAlive = true;
            }
        }
        catch (Exception exception)
        {
            failure.TrySetException(exception);
        }
    }

    private void OnReconnected(object? sender, EventArgs args)
    {
        ISession? previous = null;
        try
        {
            lock (gate)
            {
                if (!ReferenceEquals(sender, reconnect))
                {
                    return;
                }

                if (stopping)
                {
                    // An already-running SDK attempt can finish after its timer was disposed.
                    // Release a late replacement rather than reviving collection during shutdown.
                    previous = ReferenceEquals(reconnect.Session, session) ? null : reconnect.Session;
                    return;
                }

                if (reconnect.Session is { } recovered && !ReferenceEquals(recovered, session))
                {
                    previous = session;
                    previous.KeepAlive -= OnKeepAlive;
                    session = recovered;
                    Attach(session);
                }

                restored?.Invoke();
                logger.LogInformation("OPC UA session restored; active session {SessionId}, subscriptions {SubscriptionCount}",
                    session.SessionId, session.SubscriptionCount);
            }
        }
        catch (Exception exception)
        {
            failure.TrySetException(exception);
        }
        finally
        {
            previous?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ISession active;
        lock (gate)
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
            active = session;
            active.KeepAlive -= OnKeepAlive;
        }

        // Stop the SDK timer before closing the active session. Late callbacks observe stopping.
        reconnect.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
            await active.CloseAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is ServiceResultException or OperationCanceledException)
        {
            logger.LogDebug(exception, "OPC UA session close did not complete during shutdown");
        }
        finally
        {
            active.Dispose();
        }
    }
}
