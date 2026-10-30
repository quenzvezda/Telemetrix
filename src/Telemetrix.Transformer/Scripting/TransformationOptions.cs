namespace Telemetrix.Transformer.Scripting;

public sealed class TransformationOptions
{
    public const string SectionName = "Transformations";

    public bool Enabled { get; set; }
    public string Directory { get; set; } = "transformations";
    public string PythonExecutable { get; set; } = OperatingSystem.IsWindows() ? "python" : "python3";
    public int TimeoutMs { get; set; } = 2000;
    public int MaxOutputSamples { get; set; } = 10000;
    // Applied as RLIMIT_AS on POSIX only; 0 disables it.
    public int MemoryLimitMb { get; set; } = 512;
}
