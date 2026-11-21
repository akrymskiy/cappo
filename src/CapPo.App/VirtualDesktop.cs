using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CapPo.App;

/// <summary>
/// Windows' public virtual desktop API (IVirtualDesktopManager): whether a window is on the desktop in view, and
/// moving one of CapPo's windows there. A window on another desktop is cloaked: shown, yet not to be seen.
/// </summary>
internal static partial class VirtualDesktop
{
    private static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private const uint CLSCTX_ALL = 0x17;

    /// <returns>Null when Windows can't say, e.g. while Explorer isn't running.</returns>
    public static bool? IsOnCurrentDesktop(nint hwnd) =>
        Use(m => m.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) == 0 ? on != 0 : (bool?)null, null);

    /// <summary>Moves a window of this process to the desktop of <paramref name="reference"/> (e.g. the foreground window).</summary>
    public static bool MoveTo(nint hwnd, nint reference) =>
        Use(m => m.GetWindowDesktopId(reference, out var id) == 0 && id != Guid.Empty && m.MoveWindowToDesktop(hwnd, id) == 0, false);

    // The fallback when the API isn't there or fails.
    private static T Use<T>(Func<IVirtualDesktopManager, T> action, T fallback)
    {
        if (CoCreateInstance(CLSID_VirtualDesktopManager, 0, CLSCTX_ALL, typeof(IVirtualDesktopManager).GUID, out var pointer) != 0)
            return fallback;
        try
        {
            return action((IVirtualDesktopManager)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None));
        }
        catch (COMException)
        {
            return fallback;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [GeneratedComInterface]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    internal partial interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(nint topLevelWindow, in Guid desktopId);
    }
}
