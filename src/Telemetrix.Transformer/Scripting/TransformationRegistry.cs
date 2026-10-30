using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Scripting;

public sealed record OutputTag(string Unit, double? MinValid, double? MaxValid);

public sealed record RegisteredTransformation(string Label, string ScriptPath, string EntryPoint, string Source,
    string Version, JsonElement Parameters, IReadOnlyDictionary<string, OutputTag> Outputs)
{
    // Twelve hex digits identify a script version in metrics without unbounded tag values.
    public string ShortVersion => Version["sha256:".Length..][..12];
}

public sealed class TransformationConfigurationException(string message) : Exception(message);

// Labels → scripts inside one directory, read and hashed once at startup (ADR-0016).
public sealed partial class TransformationRegistry
{
    public const string FileName = "registry.json";
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private readonly Dictionary<string, RegisteredTransformation> entries;

    private TransformationRegistry(string directory, Dictionary<string, RegisteredTransformation> entries)
    {
        Directory = directory;
        this.entries = entries;
    }

    public static TransformationRegistry Empty { get; } = new("", []);
    public string Directory { get; }
    public IReadOnlyCollection<RegisteredTransformation> All => entries.Values;
    public RegisteredTransformation? Find(string label) => entries.GetValueOrDefault(label);

    public static TransformationRegistry Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var file = Path.Combine(root, FileName);
        if (!File.Exists(file)) throw Invalid($"Transformation registry {file} does not exist.");
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllBytes(file)); }
        catch (JsonException exception) { throw Invalid($"Transformation registry {file} is not valid JSON: {exception.Message}"); }
        using (document)
        {
            var top = document.RootElement;
            RequireObject(top, "The registry");
            RequireOnly(top, "The registry", "transformations");
            if (!top.TryGetProperty("transformations", out var transformations))
                throw Invalid("The registry must contain a \"transformations\" object.");
            RequireObject(transformations, "\"transformations\"");
            var entries = new Dictionary<string, RegisteredTransformation>(StringComparer.Ordinal);
            foreach (var property in transformations.EnumerateObject())
                entries.Add(property.Name, Parse(root, property.Name, property.Value));
            return new TransformationRegistry(root, entries);
        }
    }

    private static RegisteredTransformation Parse(string root, string label, JsonElement entry)
    {
        if (!TransformationLabel.IsValid(label))
            throw Invalid($"Transformation label '{label}' must match {TransformationLabel.Pattern}.");
        var name = $"Transformation '{label}'";
        RequireObject(entry, name);
        RequireOnly(entry, name, "script", "entryPoint", "sha256", "parameters", "outputs");

        var script = OptionalString(entry, "script", name)
            ?? throw Invalid($"{name} must name its script.");
        if (Path.IsPathRooted(script)) throw Invalid($"{name} script must be relative to the transformation directory.");
        var path = Path.GetFullPath(Path.Combine(root, script));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw Invalid($"{name} script must stay inside the transformation directory.");
        if (!File.Exists(path)) throw Invalid($"{name} script {path} does not exist.");

        var entryPoint = OptionalString(entry, "entryPoint", name) ?? "transform";
        if (!Identifier().IsMatch(entryPoint)) throw Invalid($"{name} entry point must be a Python identifier.");

        var bytes = File.ReadAllBytes(path);
        var version = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (OptionalString(entry, "sha256", name) is { } pin
            && !string.Equals(pin.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? pin : "sha256:" + pin,
                version, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"{name} script hash {version} does not match the pinned {pin}.");
        string source;
        try { source = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid($"{name} script must be UTF-8."); }

        var parameters = entry.TryGetProperty("parameters", out var value) && value.ValueKind != JsonValueKind.Null
            ? value : EmptyObject;
        RequireObject(parameters, $"{name} parameters");
        return new RegisteredTransformation(label, path, entryPoint, source, version, parameters.Clone(), Outputs(entry, name));
    }

    private static Dictionary<string, OutputTag> Outputs(JsonElement entry, string name)
    {
        var outputs = new Dictionary<string, OutputTag>(StringComparer.Ordinal);
        if (!entry.TryGetProperty("outputs", out var declared) || declared.ValueKind == JsonValueKind.Null) return outputs;
        RequireObject(declared, $"{name} outputs");
        foreach (var output in declared.EnumerateObject())
        {
            var outputName = $"{name} output '{output.Name}'";
            if (!TagName().IsMatch(output.Name)) throw Invalid($"{outputName} is not a valid Tag name.");
            RequireObject(output.Value, outputName);
            RequireOnly(output.Value, outputName, "unit", "minValid", "maxValid");
            var unit = OptionalString(output.Value, "unit", outputName);
            if (string.IsNullOrWhiteSpace(unit)) throw Invalid($"{outputName} must declare a unit.");
            var min = OptionalNumber(output.Value, "minValid", outputName);
            var max = OptionalNumber(output.Value, "maxValid", outputName);
            if (min > max) throw Invalid($"{outputName} minValid must not exceed maxValid.");
            outputs.Add(output.Name, new OutputTag(unit, min, max));
        }
        return outputs;
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid($"{name} must be a JSON object.");
    }

    private static void RequireOnly(JsonElement element, string name, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid($"{name} has unknown property \"{property.Name}\".");
    }

    private static string? OptionalString(JsonElement element, string property, string name) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : throw Invalid($"{name} \"{property}\" must be a string.");

    private static double? OptionalNumber(JsonElement element, string property, string name) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : throw Invalid($"{name} \"{property}\" must be a number.");

    private static TransformationConfigurationException Invalid(string message) => new(message);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagName();
}
