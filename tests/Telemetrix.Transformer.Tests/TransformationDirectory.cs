using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

// A disposable transformation directory: registry.json plus scripts.
internal sealed class TransformationDirectory : IDisposable
{
    public TransformationDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"telemetrix-scripts-{Guid.NewGuid():N}");

    public static TransformationOptions Options(int timeoutMs = 2000) => new()
    {
        Enabled = true,
        PythonExecutable = Environment.GetEnvironmentVariable("TELEMETRIX_PYTHON")
            ?? new TransformationOptions().PythonExecutable,
        TimeoutMs = timeoutMs
    };

    public TransformationDirectory Script(string file, string source)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, file), source);
        return this;
    }

    public TransformationDirectory Registry(object registry)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, TransformationRegistry.FileName), JsonSerializer.Serialize(registry));
        return this;
    }

    // One label per script, entry point `transform`, with the given outputs.
    public TransformationDirectory Single(string label, string source, object? outputs = null, object? parameters = null)
    {
        Script(label.Replace('-', '_') + ".py", source);
        return Registry(new
        {
            transformations = new Dictionary<string, object?>
            {
                [label] = new { script = label.Replace('-', '_') + ".py", parameters, outputs }
            }
        });
    }

    public TransformationRegistry Load() => TransformationRegistry.Load(Path);

    public PythonWorker Worker(TransformationOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()), Load(), NullLogger<PythonWorker>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
