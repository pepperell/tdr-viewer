using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TdrSim.App.Shell;

/// <summary>
/// Per-user application settings, persisted as JSON in %APPDATA%\TdrViewer. Everything
/// here is a convenience default, never document state: losing the file costs nothing
/// but preferences, so loading and saving are both best-effort.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Last main-window size (device-independent pixels); 0 = never saved,
    /// keep the XAML default.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>Open <see cref="LastFile"/> again on startup (unless a file is passed
    /// on the command line).</summary>
    public bool ReopenLastFile { get; set; } = true;

    /// <summary>The file that was open when the app closed; null = untitled.</summary>
    public string? LastFile { get; set; }

    /// <summary>Most recently used first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    public bool IsLive { get; set; } = true;

    // Pulse-view stimulus defaults (mirror PulseViewModel's own defaults).

    public double PulseRiseTimePs { get; set; } = 200;

    public double PulseFallTimePs { get; set; } = 200;

    public double PulseRateMegahertz { get; set; } = 1;

    public double PulseDutyPercent { get; set; } = 50;

    public int PulsesDisplayed { get; set; } = 3;
}

// Unlike the project context, unmapped members are skipped (the default): the settings
// file is shared across app versions and an unknown key must never block startup.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>Loads and saves <see cref="AppSettings"/>; any unreadable or unwritable
/// state degrades to defaults rather than an error the user can do nothing about.</summary>
public static class AppSettingsStore
{
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdrViewer");

    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(SettingsPath), AppSettingsJsonContext.Default.AppSettings)
                ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a read-only profile loses preferences, not work.
        }
    }
}
