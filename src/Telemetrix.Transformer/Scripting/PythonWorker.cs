using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Telemetrix.Transformer.Scripting;

public sealed record RuntimeResponse(string? FailureReason, string? Message, JsonElement Result)
{
    public static RuntimeResponse Failed(string reason, string message) => new(reason, message, default);
}

public interface ITransformationRuntime
{
    // Permanent outcomes come back as a response; an unavailable runtime throws TransformationRuntimeException.
    Task<RuntimeResponse> TransformAsync(RegisteredTransformation transformation, ReadOnlyMemory<byte> batch,
        ReadOnlyMemory<byte> context, CancellationToken cancellationToken);
}

public sealed class TransformationRuntimeException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// One long-lived Python child process speaking newline-delimited JSON (ADR-0016).
public sealed class PythonWorker(IOptions<TransformationOptions> options, TransformationRegistry registry,
    ILogger<PythonWorker> logger, TimeProvider? timeProvider = null) : ITransformationRuntime, IDisposable
{
    public const string ScriptError = "script-error";
    public const string MalformedOutput = "malformed-output";
    public const string Timeout = "timeout";
    public const string WorkerCrashed = "worker-crashed";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private Process? process;

    public static string RunnerPath => Path.Combine(AppContext.BaseDirectory, "Scripting", "telemetrix_runner.py");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await EnsureStartedAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<RuntimeResponse> TransformAsync(RegisteredTransformation transformation, ReadOnlyMemory<byte> batch,
        ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            var request = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(request))
            {
                writer.WriteStartObject();
                writer.WriteString("op", "transform");
                writer.WriteString("label", transformation.Label);
                writer.WritePropertyName("batch");
                writer.WriteRawValue(batch.Span, skipInputValidation: true);
                writer.WritePropertyName("context");
                writer.WriteRawValue(context.Span, skipInputValidation: true);
                writer.WriteEndObject();
            }
            var exchange = await ExchangeAsync(Utf8.GetString(request.WrittenSpan), TimeSpan.FromMilliseconds(options.Value.TimeoutMs),
                cancellationToken);
            if (exchange.Failure is { } failure) return failure;
            using var response = exchange.Response!;
            var root = response.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out var result))
                return new RuntimeResponse(null, null, result.Clone());
            var reason = root.TryGetProperty("reason", out var value) ? value.GetString() : null;
            var message = root.TryGetProperty("message", out var text) ? text.GetString() ?? "" : "";
            if (reason is ScriptError or MalformedOutput) return RuntimeResponse.Failed(reason, message);
            Stop();
            return RuntimeResponse.Failed(WorkerCrashed, $"Unexpected runner response {reason}: {message}");
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        Stop();
        gate.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (process is { HasExited: false }) return;
        Stop();
        var settings = options.Value;
        var info = new ProcessStartInfo(settings.PythonExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = registry.Directory.Length > 0 ? registry.Directory : AppContext.BaseDirectory
        };
        foreach (var argument in new[] { "-I", "-u", RunnerPath,
                     "--memory-limit-mb", settings.MemoryLimitMb.ToString(CultureInfo.InvariantCulture),
                     "--max-output-samples", settings.MaxOutputSamples.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);
        try { process = Process.Start(info) ?? throw new TransformationRuntimeException("The Python worker did not start."); }
        catch (Win32Exception exception)
        {
            throw new TransformationRuntimeException(
                $"Cannot start Python worker '{settings.PythonExecutable}': {exception.Message}", exception);
        }
        var started = process;
        _ = Task.Run(() => PumpStandardErrorAsync(started), CancellationToken.None);
        try { await HandshakeAndLoadAsync(started, cancellationToken); }
        catch
        {
            // A half-loaded worker must never serve a Batch.
            Stop();
            throw;
        }
    }

    private async Task HandshakeAndLoadAsync(Process started, CancellationToken cancellationToken)
    {
        JsonDocument? handshake;
        try { handshake = await ReadAsync(StartTimeout, cancellationToken); }
        catch (TimeoutException) { throw Unavailable("The Python worker was not ready within 15 seconds."); }

        using (var ready = handshake ?? throw Unavailable("The Python worker exited before it was ready."))
        {
            if (!ready.RootElement.TryGetProperty("contractVersion", out var version) || version.GetInt32() != 1)
                throw Unavailable("The Python worker does not speak transformation contract version 1.");
            logger.LogInformation("Started Python worker {ProcessId} with Python {PythonVersion}",
                started.Id, ready.RootElement.GetProperty("python").GetString());
        }

        foreach (var transformation in registry.All)
        {
            var load = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["op"] = "load", ["label"] = transformation.Label, ["version"] = transformation.Version,
                ["filename"] = transformation.ScriptPath, ["entryPoint"] = transformation.EntryPoint,
                ["source"] = transformation.Source
            });
            var exchange = await ExchangeAsync(load, StartTimeout, cancellationToken);
            if (exchange.Failure is { } failure)
                throw Unavailable($"Transformation '{transformation.Label}' did not load: {failure.Message}");
            using var response = exchange.Response!;
            if (!response.RootElement.GetProperty("ok").GetBoolean())
                throw Unavailable($"Transformation '{transformation.Label}' did not load: "
                    + response.RootElement.GetProperty("message").GetString());
            logger.LogInformation("Loaded transformation {Label} version {ScriptVersion}",
                transformation.Label, transformation.Version);
        }
    }

    private async Task<(JsonDocument? Response, RuntimeResponse? Failure)> ExchangeAsync(string request, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await process!.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            Stop();
            return (null, RuntimeResponse.Failed(WorkerCrashed, $"The Python worker stopped reading: {exception.Message}"));
        }
        JsonDocument? response;
        try { response = await ReadAsync(timeout, cancellationToken); }
        catch (TimeoutException)
        {
            return (null, RuntimeResponse.Failed(Timeout, $"The script did not finish within {timeout.TotalMilliseconds:0} ms; the worker was killed."));
        }
        catch (TransformationRuntimeException exception)
        {
            return (null, RuntimeResponse.Failed(WorkerCrashed, exception.Message));
        }
        return response is null
            ? (null, RuntimeResponse.Failed(WorkerCrashed, "The Python worker exited while transforming."))
            : (response, null);
    }

    // A timeout kills the worker; the pending read then completes when the pipe closes.
    private async Task<JsonDocument?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var current = process!;
        var read = current.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, clock, waiting.Token);
        if (await Task.WhenAny(read, delay) != read)
        {
            Stop();
            _ = read.ContinueWith(task => task.Exception, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        await waiting.CancelAsync();
        string? line;
        try { line = await read; }
        catch (IOException) { line = null; }
        if (line is null)
        {
            Stop();
            return null;
        }
        try { return JsonDocument.Parse(line); }
        catch (JsonException exception)
        {
            Stop();
            throw Unavailable($"The Python worker wrote a non-protocol line: {exception.Message}");
        }
    }

    private async Task PumpStandardErrorAsync(Process source)
    {
        try
        {
            while (await source.StandardError.ReadLineAsync() is { } line)
                logger.LogInformation("Python worker: {Output}", line.Length > 1024 ? line[..1024] : line);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Stop()
    {
        var current = Interlocked.Exchange(ref process, null);
        if (current is null) return;
        try
        {
            if (!current.HasExited) current.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        current.Dispose();
    }

    private static TransformationRuntimeException Unavailable(string message) => new(message);
}
