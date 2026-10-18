using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Tests;

public class QualityTests
{
    [Theory]
    [InlineData(0x00000000u, Quality.Good)]
    [InlineData(0x00300080u, Quality.Good)]
    [InlineData(0x40000000u, Quality.Uncertain)]
    [InlineData(0x40920000u, Quality.Uncertain)]
    [InlineData(0x80000000u, Quality.Bad)]
    [InlineData(0x809B0000u, Quality.Bad)]
    [InlineData(0xFFFFFFFFu, Quality.Bad)]
    public void SeverityBitsDetermineQuality(uint statusCode, Quality expected) =>
        Assert.Equal(expected, QualityMapper.Map(statusCode));

    [Theory]
    [InlineData(500d, Quality.Good, -20d, 120d, Quality.Bad)]
    [InlineData(-21d, Quality.Uncertain, -20d, 120d, Quality.Bad)]
    [InlineData(-20d, Quality.Good, -20d, 120d, Quality.Good)]
    [InlineData(120d, Quality.Good, -20d, 120d, Quality.Good)]
    [InlineData(71.3, Quality.Uncertain, -20d, 120d, Quality.Uncertain)]
    [InlineData(71.3, Quality.Bad, -20d, 120d, Quality.Bad)]
    [InlineData(null, Quality.Good, -20d, 120d, Quality.Bad)]
    [InlineData(null, Quality.Good, null, null, Quality.Bad)]
    [InlineData(500d, Quality.Good, null, null, Quality.Good)]
    [InlineData(-21d, Quality.Good, -20d, null, Quality.Bad)]
    [InlineData(500d, Quality.Good, null, 120d, Quality.Bad)]
    [InlineData(double.NaN, Quality.Good, null, null, Quality.Bad)]
    [InlineData(double.PositiveInfinity, Quality.Good, null, null, Quality.Bad)]
    public void RangeOrMissingValueCanOnlyDowngradeQuality(double? value, Quality quality,
        double? minValid, double? maxValid, Quality expected) =>
        Assert.Equal(expected, RangeCheck.Evaluate(value, quality, minValid, maxValid));
}
