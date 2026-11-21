using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace CapPo.App.RunModes;

/// <summary>
/// What starts CapPo in On demand mode: a background task on NetworkStateChange plus a timer (Store build), or a
/// per-user Task Scheduler task on "network connected" (NetworkProfile event 10000), sign-in and a repeating timer
/// (GitHub build). Either runs a short detection only; the sign-in window opens only when there's a portal.
/// </summary>
internal static class OnDemandTrigger
{
#if STORE_BUILD
    public static Task<bool> IsRegisteredAsync() => Task.FromResult(Background.NetworkCheckTask.IsRegistered);

    public static Task RegisterAsync(TimeSpan recheck) => Background.NetworkCheckTask.RegisterAsync(recheck);

    public static Task UnregisterAsync()
    {
        Background.NetworkCheckTask.Unregister();
        return Task.CompletedTask;
    }
#else
    // Per user: task names are shared by everyone on the PC.
    private static string TaskName => $"CapPo portal check ({Environment.UserName})";

    public static async Task<bool> IsRegisteredAsync() =>
        (await SchtasksAsync("/Query", "/TN", TaskName)).ExitCode == 0;

    public static async Task RegisterAsync(TimeSpan recheck)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"CapPo-task-{Environment.ProcessId}.xml");
        // schtasks reads task XML as UTF-16, as Task Scheduler exports it.
        await File.WriteAllTextAsync(xml, TaskXml(recheck), Encoding.Unicode);
        try
        {
            var (exitCode, output) = await SchtasksAsync("/Create", "/TN", TaskName, "/XML", xml, "/F");
            if (exitCode != 0)
                throw new InvalidOperationException($"Task Scheduler refused the task: {output.Trim()}");
            Log.Info($"Registered scheduled task \"{TaskName}\" (re-check every {recheck.TotalMinutes:0} min)");
        }
        finally
        {
            File.Delete(xml);
        }
    }

    public static async Task UnregisterAsync()
    {
        if (!await IsRegisteredAsync())
            return;
        var (exitCode, output) = await SchtasksAsync("/Delete", "/TN", TaskName, "/F");
        if (exitCode != 0)
            throw new InvalidOperationException($"Couldn't remove the scheduled task: {output.Trim()}");
        Log.Info($"Removed scheduled task \"{TaskName}\"");
    }

    internal static string TaskXml(TimeSpan recheck)
    {
        var user = WindowsIdentity.GetCurrent();
        var sid = user.User!.Value;
        var exe = SecurityElement.Escape(AppInfo.ExePath);
        var subscription = SecurityElement.Escape(
            "<QueryList><Query Id=\"0\" Path=\"Microsoft-Windows-NetworkProfile/Operational\">" +
            "<Select Path=\"Microsoft-Windows-NetworkProfile/Operational\">" +
            "*[System[Provider[@Name='Microsoft-Windows-NetworkProfile'] and EventID=10000]]</Select></Query></QueryList>");
        var interval = $"PT{(int)recheck.TotalMinutes}M";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{AppInfo.Name}</Author>
                <Description>Checks for a captive portal (hotel or library Wi-Fi sign-in) when a network connects, and shows its sign-in page if there is one. Made by CapPo's On demand mode; CapPo removes it when switched to Always running, or with CapPo.exe --unregister.</Description>
              </RegistrationInfo>
              <Triggers>
                <EventTrigger>
                  <Enabled>true</Enabled>
                  <Subscription>{subscription}</Subscription>
                  <Delay>PT2S</Delay>
                </EventTrigger>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
                <TimeTrigger>
                  <Enabled>true</Enabled>
                  <StartBoundary>2026-01-01T00:00:00</StartBoundary>
                  <Repetition>
                    <Interval>{interval}</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>--check</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static async Task<(int ExitCode, string Output)> SchtasksAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start schtasks.exe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
#endif
}
