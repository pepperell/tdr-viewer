using System.Text.Json;

namespace TdrSim.Core.Project;

/// <summary>Round-trips <see cref="ProjectDocument"/> to the .tdrproj JSON format.</summary>
public static class ProjectSerializer
{
    public const string FileExtension = ".tdrproj";

    public static string Serialize(ProjectDocument document)
        => JsonSerializer.Serialize(document, ProjectJsonContext.Default.ProjectDocument);

    /// <summary>Throws <see cref="InvalidDataException"/> with a user-presentable
    /// message on anything that is not a readable project file (schema versions
    /// 1..current; older versions are strict subsets of the current format).</summary>
    public static ProjectDocument Deserialize(string json)
    {
        int version = PeekSchemaVersion(json);
        if (version is < ProjectDocument.OldestReadableSchemaVersion or > ProjectDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported project schema version {version} - this build reads versions " +
                $"{ProjectDocument.OldestReadableSchemaVersion} to {ProjectDocument.CurrentSchemaVersion}; " +
                "the file may come from a newer TDR Viewer.");
        }

        try
        {
            return JsonSerializer.Deserialize(json, ProjectJsonContext.Default.ProjectDocument)
                ?? throw new InvalidDataException("Project file deserialized to null.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Project file is malformed: {e.Message}", e);
        }
    }

    /// <summary>The built-in demo project (the M2 tee network) shown on first launch.</summary>
    public static ProjectDocument LoadEmbeddedDefault()
    {
        const string resourceName = "TdrSim.Core.Project.Data.default.tdrproj";
        using var stream = typeof(ProjectSerializer).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return Deserialize(reader.ReadToEnd());
    }

    // The version is checked before the full parse so a file from a future TDR Viewer
    // fails with "newer version", not with whatever unknown member its parser hits first.
    private static int PeekSchemaVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("schemaVersion", out var versionElement)
                || !versionElement.TryGetInt32(out int version))
            {
                throw new InvalidDataException("Not a TDR Viewer project file (no schemaVersion).");
            }

            return version;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Project file is not valid JSON: {e.Message}", e);
        }
    }
}
