using Opc.Ua;
using Opc.Ua.Server;
using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim.Server;

public sealed class PlantNodeManager : CustomNodeManager2
{
    public const string PlantNamespace = "urn:telemetrix:plantsim";
    private readonly PumpingLine line;
    private readonly TimeProvider timeProvider;
    private readonly Random rng;
    private readonly Func<uint, bool> restart;
    private readonly Dictionary<string, BaseDataVariableState> tags = [];

    public PlantNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        PumpingLine line, TimeProvider timeProvider, int seed, Func<uint, bool> restart)
        : base(server, configuration, PlantNamespace)
    {
        this.line = line;
        this.timeProvider = timeProvider;
        rng = new Random(seed);
        this.restart = restart;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (NamespaceIndex != 2) throw new InvalidOperationException("PlantSim requires namespace index 2.");
            var plant = Object(null, "Plant01", "Plant01", ObjectTypeIds.FolderType);
            plant.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = [];
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, plant.NodeId));
            var productionLine = Object(plant, "Line01", "Plant01.Line01", ObjectTypeIds.FolderType);
            foreach (var asset in PlantTags.All.GroupBy(tag => tag.Asset))
            {
                var assetObject = Object(productionLine, asset.Key, $"Line01.{asset.Key}", ObjectTypeIds.BaseObjectType);
                foreach (var tag in asset)
                {
                    var variable = new BaseDataVariableState(assetObject)
                    {
                        NodeId = new NodeId(tag.Identifier, NamespaceIndex),
                        BrowseName = new QualifiedName(tag.Name, NamespaceIndex),
                        DisplayName = new LocalizedText(tag.Name),
                        ReferenceTypeId = ReferenceTypeIds.HasComponent,
                        TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                        DataType = tag.IsBoolean ? DataTypeIds.Boolean : DataTypeIds.Double,
                        ValueRank = ValueRanks.Scalar,
                        AccessLevel = AccessLevels.CurrentRead,
                        UserAccessLevel = AccessLevels.CurrentRead,
                        MinimumSamplingInterval = 1000,
                        Value = tag.IsBoolean ? true : tag.Minimum,
                        StatusCode = StatusCodes.Good,
                        Timestamp = timeProvider.GetUtcNow().UtcDateTime
                    };
                    assetObject.AddChild(variable);
                    tags[tag.Key] = variable;
                    if (!tag.IsBoolean)
                    {
                        var units = new PropertyState(variable)
                        {
                            NodeId = new NodeId($"{tag.Identifier}.EngineeringUnits", NamespaceIndex),
                            BrowseName = new QualifiedName(BrowseNames.EngineeringUnits),
                            DisplayName = new LocalizedText(BrowseNames.EngineeringUnits),
                            ReferenceTypeId = ReferenceTypeIds.HasProperty,
                            TypeDefinitionId = VariableTypeIds.PropertyType,
                            DataType = DataTypeIds.EUInformation,
                            ValueRank = ValueRanks.Scalar,
                            AccessLevel = AccessLevels.CurrentRead,
                            UserAccessLevel = AccessLevels.CurrentRead,
                            Value = new ExtensionObject(new EUInformation
                            {
                                NamespaceUri = "http://www.opcfoundation.org/UA/units/un/cefact",
                                UnitId = UnitId(tag.Unit), DisplayName = new LocalizedText(tag.Unit),
                                Description = new LocalizedText(tag.Unit)
                            })
                        };
                        variable.AddChild(units);
                    }
                }
            }
            var simulation = Object(productionLine, "Simulation", "Line01.Simulation", ObjectTypeIds.BaseObjectType);
            SimulationMethods.Add(simulation, NamespaceIndex, line, restart);
            Update();
            AddPredefinedNode(SystemContext, plant);
        }
    }

    public void Update()
    {
        lock (Lock)
        {
            var timestamp = timeProvider.GetUtcNow().UtcDateTime;
            timestamp = new DateTime(timestamp.Ticks - timestamp.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            foreach (var sample in line.Step(TimeSpan.FromSeconds(1), rng))
            {
                var variable = tags[sample.Tag.Key];
                variable.Value = sample.Value switch
                {
                    bool value => new Variant(value),
                    double value => new Variant(value),
                    _ => throw new InvalidOperationException("PlantSim only emits Boolean and Double values.")
                };
                variable.StatusCode = sample.IsBad ? StatusCodes.BadSensorFailure : StatusCodes.Good;
                variable.Timestamp = timestamp;
                variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    private BaseObjectState Object(NodeState? parent, string name, string identifier, NodeId type)
    {
        var result = new BaseObjectState(parent)
        {
            NodeId = new NodeId(identifier, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = type
        };
        parent?.AddChild(result);
        return result;
    }

    private static int UnitId(string unit) => unit switch
    {
        "m³/h" => 0x4D3150, // M1P
        "bar" => 0x424152, // BAR
        "°C" => 0x43454C, // CEL
        "mm/s" => 0x433136, // C16
        "%" => 0x5031, // P1
        _ => -1
    };
}
