using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapPo.App;

public enum RunMode
{
    /// <summary>In the notification area all the time, watching for network changes.</summary>
    AlwaysRunning,
    /// <summary>Started by Windows when a network connects (and now and then), only long enough to check.</summary>
    OnDemand,
}

/// <summary>CapPo's preferences, in settings.json in <see cref="AppInfo.DataDirectory"/>.</summary>
public sealed class AppSettings
{
    /// <summary>The choices for <see cref="RecheckMinutes"/>. Background tasks can't run more often than every 15.</summary>
    public static readonly int[] RecheckChoices = [15, 20, 30, 60];

    private static string FilePath => Path.Combine(AppInfo.DataDirectory, "settings.json");

    [JsonConverter(typeof(JsonStringEnumConverter<RunMode>))]
    public RunMode Mode { get; set; } = RunMode.AlwaysRunning;

    /// <summary>In Always running mode, start in the notification area at sign-in.</summary>
    public bool LaunchAtLogin { get; set; } = true;

    /// <summary>
    /// How often to check again while online, for portals that capture you again when a session expires.
    /// </summary>
    public int RecheckMinutes { get; set; } = 20;

    [JsonIgnore]
    public TimeSpan RecheckInterval => TimeSpan.FromMinutes(Math.Max(RecheckMinutes, RecheckChoices[0]));

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Couldn't read settings; using the defaults");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Couldn't save settings");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
