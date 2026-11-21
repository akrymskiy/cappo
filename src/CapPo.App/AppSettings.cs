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
    /// <summary>The choices for <see cref="RecheckMinutes"/>.</summary>
    public static readonly int[] RecheckChoices = [5, 15, 20, 30, 60];

    /// <summary>
    /// On demand mode's floor. The Store build's background task timer won't run more often than every 15 minutes; the
    /// GitHub build's scheduled task can, and its check (<c>--check</c>) is quick.
    /// </summary>
#if STORE_BUILD
    public const int OnDemandMinimumMinutes = 15;
#else
    public const int OnDemandMinimumMinutes = 5;
#endif

    private static string FilePath => Path.Combine(AppInfo.DataDirectory, "settings.json");

    [JsonConverter(typeof(JsonStringEnumConverter<RunMode>))]
    public RunMode Mode { get; set; } = RunMode.AlwaysRunning;

    /// <summary>In Always running mode, start in the notification area at sign-in.</summary>
    public bool LaunchAtLogin { get; set; } = true;

    /// <summary>
    /// How often to check again while online, for portals that capture you again when a session expires.
    /// </summary>
    public int RecheckMinutes { get; set; } = 20;

    /// <summary>GitHub build: CapPo has made its Start menu shortcut, so one missing now was deleted by the user.</summary>
    public bool StartMenuShortcutMade { get; set; }

    [JsonIgnore]
    public TimeSpan RecheckInterval => TimeSpan.FromMinutes(Math.Max(RecheckMinutes, RecheckChoices[0]));

    /// <summary>How often On demand mode has Windows start CapPo: the re-check interval, but not under the floor.</summary>
    [JsonIgnore]
    public int OnDemandMinutes => Math.Max(RecheckMinutes, OnDemandMinimumMinutes);

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
