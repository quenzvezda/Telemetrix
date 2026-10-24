using Opc.Ua;
using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim.Server;

internal static class SimulationMethods
{
    public static void Add(BaseObjectState simulation, ushort namespaceIndex, PumpingLine line,
        Func<uint, bool> restart)
    {
        AddMethod("TripPump", [], _ => { line.TripPump(); return ServiceResult.Good; });
        AddMethod("StartPump", [], _ => { line.StartPump(); return ServiceResult.Good; });
        AddMethod("SetBadQuality", [Argument("tag", DataTypeIds.String), Argument("seconds", DataTypeIds.UInt32)],
            inputs =>
            {
                var seconds = inputs[1].GetUInt32();
                if (seconds is 0 or > 86400) return StatusCodes.BadOutOfRange;
                line.SetBadQuality(inputs[0].GetString()!, TimeSpan.FromSeconds(seconds));
                return ServiceResult.Good;
            });
        AddMethod("Spike", [Argument("tag", DataTypeIds.String)], inputs =>
        {
            line.Spike(inputs[0].GetString()!);
            return ServiceResult.Good;
        });
        AddMethod("RestartServer", [Argument("delaySeconds", DataTypeIds.UInt32)], inputs =>
        {
            var seconds = inputs[0].GetUInt32();
            if (seconds > 5) return StatusCodes.BadOutOfRange;
            return restart(seconds) ? ServiceResult.Good : StatusCodes.BadInvalidState;
        });

        void AddMethod(string name, Argument[] arguments, Func<ArrayOf<Variant>, ServiceResult> callback)
        {
            var method = new MethodState(simulation)
            {
                NodeId = new NodeId($"Line01.Simulation.{name}", namespaceIndex),
                BrowseName = new QualifiedName(name, namespaceIndex),
                DisplayName = new LocalizedText(name),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                Executable = true,
                UserExecutable = true
            };
            if (arguments.Length > 0)
            {
                method.InputArguments = PropertyState<ArrayOf<Argument>>.With<StructureBuilder<Argument>>(method);
                method.InputArguments.NodeId = new NodeId($"Line01.Simulation.{name}.InputArguments", namespaceIndex);
                method.InputArguments.BrowseName = new QualifiedName(BrowseNames.InputArguments);
                method.InputArguments.DisplayName = new LocalizedText(BrowseNames.InputArguments);
                method.InputArguments.DataType = DataTypeIds.Argument;
                method.InputArguments.ValueRank = ValueRanks.OneDimension;
                method.InputArguments.Value = arguments;
            }
            method.OnCallMethod = (_, _, inputs, _) =>
            {
                try { return callback(inputs); }
                catch (ArgumentException) { return StatusCodes.BadInvalidArgument; }
            };
            simulation.AddChild(method);
        }
    }

    private static Argument Argument(string name, NodeId type) => new()
    {
        Name = name, DataType = type, ValueRank = ValueRanks.Scalar, Description = new LocalizedText(name)
    };
}
