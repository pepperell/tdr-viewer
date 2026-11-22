using System.Text.Json.Serialization;

namespace TdrSim.Core.Catalog;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(CatalogFile<CableSpec>))]
[JsonSerializable(typeof(CatalogFile<ConnectorSpec>))]
[JsonSerializable(typeof(CatalogFile<InstrumentSpec>))]
[JsonSerializable(typeof(CatalogFile<FaultSpec>))]
[JsonSerializable(typeof(CatalogFile<ConnectorFamilySpec>))]
[JsonSerializable(typeof(CatalogFile<AdapterSpec>))]
public sealed partial class CatalogJsonContext : JsonSerializerContext;
