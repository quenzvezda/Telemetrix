namespace Telemetrix.Transformer.Pipeline;

public sealed class TagResolutionOptions
{
    public const string SectionName = "TagResolution";
    public const string DeadLetter = "dead-letter";
    public const string Create = "create";

    public string UnknownTagPolicy { get; set; } = DeadLetter;
}
