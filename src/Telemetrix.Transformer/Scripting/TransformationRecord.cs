using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Scripting;

public sealed record SampleDisposition(int Index, int TagId, DateTimeOffset SourceTimestamp, string Disposition,
    string Reason);

// One durable transformation outcome per Batch and label, written with the Batch's Measurements (ADR-0018).
public sealed record TransformationRecord(string Label, string AssetId, string? ScriptVersion, string Status,
    string? Reason, string? Error, int InputCount, int UnchangedCount, int ModifiedCount, int FlaggedCount,
    int RejectedCount, IReadOnlyList<ValidatedSample> Derived, IReadOnlyList<SampleDisposition> Dispositions,
    double DurationMs)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string UnknownLabel = "unknown-label";
    public const string OutputTagConflict = "output-tag-conflict";

    public int OutputCount => Derived.Count;
    public string Outcome => Reason ?? Succeeded;
}
