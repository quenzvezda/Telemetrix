using Opc.Ua;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

internal static class OpcUaSampleMapper
{
    public static Sample Map(OpcUaOptions options, OpcUaTagOptions tag, DataValue notification)
    {
        var value = notification.WrappedValue;
        double? number = value.TypeInfo.ValueRank != ValueRanks.Scalar ? null : value.TypeInfo.BuiltInType switch
        {
            BuiltInType.Boolean => value.GetBoolean() ? 1 : 0,
            BuiltInType.SByte => value.GetSByte(),
            BuiltInType.Byte => value.GetByte(),
            BuiltInType.Int16 => value.GetInt16(),
            BuiltInType.UInt16 => value.GetUInt16(),
            BuiltInType.Int32 => value.GetInt32(),
            BuiltInType.UInt32 => value.GetUInt32(),
            BuiltInType.Int64 => value.GetInt64(),
            BuiltInType.UInt64 => value.GetUInt64(),
            BuiltInType.Float => value.GetFloat(),
            BuiltInType.Double => value.GetDouble(),
            _ => null
        };

        return new Sample(options.Plant, options.Line, tag.Asset, tag.Tag, number,
            notification.SourceTimestamp.ToDateTimeOffset(),
            notification.ServerTimestamp.ToDateTimeOffset(), notification.StatusCode.Code);
    }
}
