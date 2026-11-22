using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TdrSim.Core.Catalog;

public static class CatalogLoader
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Loads the built-in catalog from embedded resources. Throws
    /// <see cref="InvalidDataException"/> on malformed or missing data.</summary>
    public static ComponentCatalog LoadEmbedded()
    {
        return new ComponentCatalog
        {
            Cables = LoadFile("cables.json", CatalogJsonContext.Default.CatalogFileCableSpec).Items,
            Connectors = LoadFile("connectors.json", CatalogJsonContext.Default.CatalogFileConnectorSpec).Items,
            Instruments = LoadFile("instruments.json", CatalogJsonContext.Default.CatalogFileInstrumentSpec).Items,
            Faults = LoadFile("faults.json", CatalogJsonContext.Default.CatalogFileFaultSpec).Items,
        };
    }

    public static CatalogFile<T> Deserialize<T>(string json, JsonTypeInfo<CatalogFile<T>> typeInfo)
    {
        var file = JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new InvalidDataException("Catalog JSON deserialized to null.");
        if (file.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported catalog schema version {file.SchemaVersion} (expected {CurrentSchemaVersion}).");
        }

        return file;
    }

    public static string Serialize<T>(CatalogFile<T> file, JsonTypeInfo<CatalogFile<T>> typeInfo)
        => JsonSerializer.Serialize(file, typeInfo);

    private static CatalogFile<T> LoadFile<T>(string fileName, JsonTypeInfo<CatalogFile<T>> typeInfo)
    {
        var resourceName = $"TdrSim.Core.Catalog.Data.{fileName}";
        using var stream = typeof(CatalogLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded catalog resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return Deserialize(reader.ReadToEnd(), typeInfo);
    }
}
