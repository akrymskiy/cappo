using Microsoft.Win32;

namespace CapPo.App.RunModes;

public enum LaunchAtLoginState
{
    Off,
    On,
    /// <summary>Turned off in Task Manager (Startup apps) or Settings; only the user can turn it back on there.</summary>
    DisabledByUser,
}

/// <summary>
/// Starting CapPo in the notification area at sign-in: the package's startup task (Store build), or the HKCU Run key
/// (GitHub build). Neither needs administrator rights.
/// </summary>
internal static class LaunchAtLogin
{
#if STORE_BUILD
    private const string TaskId = "CapPoStartup";

    public static async Task<LaunchAtLoginState> GetAsync()
    {
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        return Map(task.State);
    }

    public static async Task<LaunchAtLoginState> SetAsync(bool on)
    {
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        if (on)
            return Map(await task.RequestEnableAsync());
        task.Disable();
        return Map(task.State);
    }

    private static LaunchAtLoginState Map(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => LaunchAtLoginState.On,
        Windows.ApplicationModel.StartupTaskState.DisabledByUser or Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => LaunchAtLoginState.DisabledByUser,
        _ => LaunchAtLoginState.Off,
    };
#else
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager records "Disabled" for Run key entries: the first byte is odd when disabled.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = AppInfo.Name;

    private static string Command => $"\"{AppInfo.ExePath}\" --startup";

    public static Task<LaunchAtLoginState> GetAsync()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string)
            return Task.FromResult(LaunchAtLoginState.Off);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return Task.FromResult(approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1
            ? LaunchAtLoginState.DisabledByUser
            : LaunchAtLoginState.On);
    }

    public static Task<LaunchAtLoginState> SetAsync(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            // Written each time, so it follows the app if its folder was moved.
            run.SetValue(ValueName, Command);
        else
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        return GetAsync();
    }
#endif
}
