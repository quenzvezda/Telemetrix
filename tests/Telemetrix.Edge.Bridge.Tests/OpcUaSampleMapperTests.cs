using Opc.Ua;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Tests;

public class OpcUaSampleMapperTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(0x40000000u)]
    [InlineData(0x80000000u)]
    public void PreservesIdentityTimestampsAndRawStatusForEveryQuality(uint statusCode)
    {
        var options = new OpcUaOptions { Plant = "Plant01", Line = "Line01" };
        var tag = new OpcUaTagOptions { Asset = "Pump01", Tag = "MotorTemperature" };
        var source = new DateTimeOffset(2026, 10, 18, 5, 15, 1, TimeSpan.Zero);
        var notification = new DataValue(new Variant(71.3), statusCode,
            source, source.AddMilliseconds(4));

        var sample = OpcUaSampleMapper.Map(options, tag, notification);

        Assert.Equal("Plant01", sample.Plant);
        Assert.Equal("Line01", sample.Line);
        Assert.Equal("Pump01", sample.Asset);
        Assert.Equal("MotorTemperature", sample.Tag);
        Assert.Equal(71.3, sample.Value);
        Assert.Equal(source, sample.SourceTimestamp);
        Assert.Equal(source.AddMilliseconds(4), sample.ServerTimestamp);
        Assert.Equal(statusCode, sample.StatusCode);
    }

    [Theory]
    [InlineData(true, 1d)]
    [InlineData(false, 0d)]
    public void MapsBooleanToNumericValue(bool value, double expected)
    {
        var sample = OpcUaSampleMapper.Map(new OpcUaOptions(), new OpcUaTagOptions(),
            new DataValue(new Variant(value)));

        Assert.Equal(expected, sample.Value);
    }

    [Fact]
    public void MissingValueStillEmitsASampleWithItsBadStatus()
    {
        var sample = OpcUaSampleMapper.Map(new OpcUaOptions(), new OpcUaTagOptions(),
            DataValue.FromStatusCode(StatusCodes.BadNoData));

        Assert.Null(sample.Value);
        Assert.Equal(StatusCodes.BadNoData.Code, sample.StatusCode);
    }
}
