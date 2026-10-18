namespace Telemetrix.Transformer.Pipeline;

public static class QualityMapper
{
    public static Quality Map(uint statusCode) => (statusCode >> 30) switch
    {
        0 => Quality.Good,
        1 => Quality.Uncertain,
        _ => Quality.Bad
    };
}
