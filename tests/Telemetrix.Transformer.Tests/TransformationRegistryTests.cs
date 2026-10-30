using System.Security.Cryptography;
using System.Text;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

public class TransformationRegistryTests
{
    private const string Source = "def transform(batch, context):\n    return {}\n";

    [Fact]
    public void ValidRegistryPinsEachScriptByContentHash()
    {
        using var directory = new TransformationDirectory()
            .Script("convert.py", Source)
            .Registry(new
            {
                transformations = new Dictionary<string, object>
                {
                    ["celsius-to-fahrenheit"] = new
                    {
                        script = "convert.py",
                        parameters = new { sourceUnit = "degC" },
                        outputs = new { TemperatureF = new { unit = "degF", minValid = -4, maxValid = 176 } }
                    }
                }
            });

        var registry = directory.Load();

        var entry = Assert.Single(registry.All);
        Assert.Same(entry, registry.Find("celsius-to-fahrenheit"));
        Assert.Null(registry.Find("pump-cleaning"));
        Assert.Equal("transform", entry.EntryPoint);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source))), entry.Version);
        Assert.Equal(12, entry.ShortVersion.Length);
        Assert.Equal("degC", entry.Parameters.GetProperty("sourceUnit").GetString());
        Assert.Equal(new OutputTag("degF", -4, 176), entry.Outputs["TemperatureF"]);
        Assert.Equal(Source, entry.Source);
    }

    [Fact]
    public void ScriptChangesAfterLoadingDoNotChangeTheLoadedVersion()
    {
        using var directory = new TransformationDirectory().Single("pump-cleaning", Source);
        var loaded = directory.Load().Find("pump-cleaning")!;

        directory.Script("pump_cleaning.py", Source + "# edited\n");

        Assert.Equal(Source, loaded.Source);
        Assert.NotEqual(loaded.Version, directory.Load().Find("pump-cleaning")!.Version);
    }

    [Theory]
    [InlineData("missing-script", "does not exist")]
    [InlineData("escaping-script", "inside the transformation directory")]
    [InlineData("absolute-script", "relative")]
    [InlineData("bad-label", "must match")]
    [InlineData("bad-entry", "Python identifier")]
    [InlineData("pin-mismatch", "does not match the pinned")]
    [InlineData("output-without-unit", "must declare a unit")]
    [InlineData("bad-output-name", "not a valid Tag name")]
    [InlineData("inverted-range", "must not exceed")]
    [InlineData("unknown-property", "unknown property")]
    [InlineData("parameters-array", "parameters must be a JSON object")]
    [InlineData("no-registry", "does not exist")]
    public void InvalidEntriesFailWithAMessageNamingTheProblem(string kind, string expected)
    {
        using var directory = new TransformationDirectory().Script("ok.py", Source);
        object entry = kind switch
        {
            "missing-script" => new { script = "missing.py" },
            "escaping-script" => new { script = "../outside.py" },
            "absolute-script" => new { script = Path.Combine(directory.Path, "ok.py") },
            "bad-entry" => new { script = "ok.py", entryPoint = "os.system" },
            "pin-mismatch" => new { script = "ok.py", sha256 = new string('0', 64) },
            "output-without-unit" => new { script = "ok.py", outputs = new { LevelNorm = new { minValid = 0 } } },
            "bad-output-name" => new { script = "ok.py", outputs = new Dictionary<string, object> { ["../x"] = new { unit = "1" } } },
            "inverted-range" => new { script = "ok.py", outputs = new { LevelNorm = new { unit = "1", minValid = 1, maxValid = 0 } } },
            "unknown-property" => new { script = "ok.py", executable = "/bin/sh" },
            "parameters-array" => new { script = "ok.py", parameters = new[] { 1 } },
            _ => new { script = "ok.py" }
        };
        if (kind != "no-registry")
            directory.Registry(new { transformations = new Dictionary<string, object> { [kind == "bad-label" ? "Bad_Label" : "label"] = entry } });

        var exception = Assert.Throws<TransformationConfigurationException>(directory.Load);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingPinIsAccepted()
    {
        using var directory = new TransformationDirectory().Script("ok.py", Source);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source)));
        directory.Registry(new { transformations = new Dictionary<string, object> { ["label"] = new { script = "ok.py", sha256 = "sha256:" + hash.ToUpperInvariant() } } });

        Assert.Equal("sha256:" + hash, Assert.Single(directory.Load().All).Version);
    }
}
