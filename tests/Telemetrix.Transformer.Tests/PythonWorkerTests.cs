using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

public class PythonWorkerTests
{
    private static readonly byte[] Batch = Encoding.UTF8.GetBytes("""{"samples":[{"index":0,"value":2.5}]}""");
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("""{"contractVersion":1,"parameters":{"factor":2}}""");

    [Fact]
    public async Task ResultReachesTheTransformerEvenWhenTheScriptPrints()
    {
        using var directory = new TransformationDirectory().Single("double", """
            import os, sys
            def transform(batch, context):
                print("noise on stdout")
                os.write(1, b"raw noise on fd 1\n")
                value = batch["samples"][0]["value"] * context["parameters"]["factor"]
                return {"samples": [{"tag": "Doubled", "value": value, "inputs": [0]}]}
            """);
        using var worker = directory.Worker();

        var response = await worker.TransformAsync(directory.Load().Find("double")!, Batch, Context, TestContext.Current.CancellationToken);

        Assert.Null(response.FailureReason);
        Assert.Equal(5, response.Result.GetProperty("samples")[0].GetProperty("value").GetDouble());
    }

    [Theory]
    [InlineData("raise ValueError('bad parameter')", PythonWorker.ScriptError, "bad parameter")]
    [InlineData("raise SystemExit(3)", PythonWorker.ScriptError, "SystemExit")]
    [InlineData("return None", PythonWorker.MalformedOutput, "must return a dict")]
    [InlineData("return {'samples': [{'value': float('nan')}]}", PythonWorker.MalformedOutput, "not valid JSON")]
    [InlineData("return {'samples': [{'value': object()}]}", PythonWorker.MalformedOutput, "not valid JSON")]
    [InlineData("return {'samples': [{}, {}, {}]}", PythonWorker.MalformedOutput, "exceed the limit")]
    public async Task PermanentScriptFailuresAreReportedAndTheWorkerKeepsServing(string body, string reason, string message)
    {
        using var directory = new TransformationDirectory().Script("bad.py", $"def transform(batch, context):\n    {body}\n")
            .Script("ok.py", "def transform(batch, context):\n    return {}\n")
            .Registry(new { transformations = new Dictionary<string, object> { ["bad"] = new { script = "bad.py" }, ["ok"] = new { script = "ok.py" } } });
        var options = TransformationDirectory.Options();
        options.MaxOutputSamples = 2;
        using var worker = directory.Worker(options);
        var registry = directory.Load();

        var failed = await worker.TransformAsync(registry.Find("bad")!, Batch, Context, TestContext.Current.CancellationToken);
        var next = await worker.TransformAsync(registry.Find("ok")!, Batch, Context, TestContext.Current.CancellationToken);

        Assert.Equal(reason, failed.FailureReason);
        Assert.Contains(message, failed.Message, StringComparison.Ordinal);
        Assert.Null(next.FailureReason);
    }

    [Fact]
    public async Task TimeoutKillsTheWorkerAndTheNextBatchGetsAFreshOne()
    {
        using var directory = new TransformationDirectory().Script("slow.py", """
            import time
            def transform(batch, context):
                if context["parameters"].get("sleep"):
                    time.sleep(30)
                return {}
            """).Registry(new { transformations = new Dictionary<string, object> { ["slow"] = new { script = "slow.py" } } });
        using var worker = directory.Worker(TransformationDirectory.Options(timeoutMs: 300));
        var slow = directory.Load().Find("slow")!;

        var started = DateTime.UtcNow;
        var timedOut = await worker.TransformAsync(slow, Batch, """{"parameters":{"sleep":true}}"""u8.ToArray(), TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - started;
        var next = await worker.TransformAsync(slow, Batch, """{"parameters":{}}"""u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(PythonWorker.Timeout, timedOut.FailureReason);
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.Null(next.FailureReason);
    }

    [Fact]
    public async Task ProcessExitIsAWorkerCrashAndTheWorkerRestarts()
    {
        using var directory = new TransformationDirectory().Single("crash", """
            import os
            def transform(batch, context):
                if context["parameters"].get("crash"):
                    os._exit(9)
                return {"samples": []}
            """);
        using var worker = directory.Worker();
        var crash = directory.Load().Find("crash")!;

        var crashed = await worker.TransformAsync(crash, Batch, """{"parameters":{"crash":true}}"""u8.ToArray(), TestContext.Current.CancellationToken);
        var next = await worker.TransformAsync(crash, Batch, """{"parameters":{}}"""u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(PythonWorker.WorkerCrashed, crashed.FailureReason);
        Assert.Null(next.FailureReason);
    }

    [Theory]
    [InlineData("def transform(batch, context)\n    return {}\n", "SyntaxError")]
    [InlineData("def other(batch, context):\n    return {}\n", "is not a callable")]
    [InlineData("raise ImportError('numpy is not installed')\n", "numpy is not installed")]
    public async Task LoadFailuresStopStartupWithTheLabel(string source, string expected)
    {
        using var directory = new TransformationDirectory().Single("broken", source);
        using var worker = directory.Worker();

        var exception = await Assert.ThrowsAsync<TransformationRuntimeException>(
            () => worker.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("'broken'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingInterpreterIsAnUnavailableRuntime()
    {
        using var directory = new TransformationDirectory().Single("ok", "def transform(batch, context):\n    return {}\n");
        var options = TransformationDirectory.Options();
        options.PythonExecutable = "telemetrix-no-such-python";
        using var worker = new PythonWorker(Options.Create(options), directory.Load(), NullLogger<PythonWorker>.Instance);

        var exception = await Assert.ThrowsAsync<TransformationRuntimeException>(() => worker.TransformAsync(
            directory.Load().Find("ok")!, Batch, Context, TestContext.Current.CancellationToken));

        Assert.Contains("telemetrix-no-such-python", exception.Message, StringComparison.Ordinal);
    }
}
