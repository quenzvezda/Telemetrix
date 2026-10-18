namespace Telemetrix.Transformer.Pipeline;

public static class RangeCheck
{
    public static Quality Evaluate(double? value, Quality quality, double? minValid, double? maxValid)
    {
        if (value is not { } number || !double.IsFinite(number)
            || (minValid is { } minimum && number < minimum)
            || (maxValid is { } maximum && number > maximum))
        {
            return Quality.Bad;
        }

        return quality;
    }
}
