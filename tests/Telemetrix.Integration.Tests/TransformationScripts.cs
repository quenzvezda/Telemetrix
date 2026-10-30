using System.Text.Json.Nodes;

namespace Telemetrix.Integration.Tests;

// The shipped examples copied to a temporary folder, plus test-only scripts registered beside them.
internal sealed class TransformationScripts : IDisposable
{
    private readonly JsonObject transformations;

    public TransformationScripts()
    {
        Directory.CreateDirectory(Path);
        foreach (var file in Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "transformations")))
            File.Copy(file, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(file)));
        transformations = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(Path, "registry.json")))!["transformations"]!.AsObject();
    }

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"telemetrix-m10-{Guid.NewGuid():N}");

    public TransformationScripts Add(string label, string body, JsonObject? outputs = null)
    {
        var file = label.Replace('-', '_') + ".py";
        File.WriteAllText(System.IO.Path.Combine(Path, file), $"def transform(batch, context):\n{body}\n");
        transformations[label] = new JsonObject { ["script"] = file, ["outputs"] = outputs };
        File.WriteAllText(System.IO.Path.Combine(Path, "registry.json"),
            new JsonObject { ["transformations"] = transformations.DeepClone() }.ToJsonString());
        return this;
    }

    public void Replace(string file, string source) => File.WriteAllText(System.IO.Path.Combine(Path, file), source);

    public Dictionary<string, string?> Settings(int timeoutMs = 2000) => new()
    {
        ["Transformations:Enabled"] = "true",
        ["Transformations:Directory"] = Path,
        ["Transformations:TimeoutMs"] = timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Transformations:PythonExecutable"] = Environment.GetEnvironmentVariable("TELEMETRIX_PYTHON")
            ?? (OperatingSystem.IsWindows() ? "python" : "python3")
    };

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
