namespace CapPo.App.RunModes;

/// <summary>Makes Windows' registrations match the chosen mode. Safe to repeat; done at every start.</summary>
internal static class RunModeSetup
{
    /// <returns>Null when done, or what went wrong, to show in Settings.</returns>
    public static async Task<string?> ApplyAsync(AppSettings settings)
    {
        List<string> problems = [];
        try
        {
            await LaunchAtLogin.SetAsync(settings.Mode == RunMode.AlwaysRunning && settings.LaunchAtLogin);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't change launch at login");
            problems.Add($"Couldn't change launch at login: {ex.Message}");
        }

        try
        {
            if (settings.Mode == RunMode.OnDemand)
                await OnDemandTrigger.RegisterAsync(settings.RecheckInterval);
            else
                await OnDemandTrigger.UnregisterAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't set up On demand mode");
            problems.Add(settings.Mode == RunMode.OnDemand
                ? $"Windows wouldn't set up On demand mode: {ex.Message}"
                : $"Couldn't remove On demand mode's trigger: {ex.Message}");
        }
        return problems.Count == 0 ? null : string.Join("\n", problems);
    }

    /// <summary>--unregister: leaves nothing behind, e.g. before deleting the GitHub build's folder.</summary>
    public static async Task RemoveAllAsync()
    {
        await LaunchAtLogin.SetAsync(false);
        await OnDemandTrigger.UnregisterAsync();
    }
}
