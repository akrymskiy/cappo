#if STORE_BUILD
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.ApplicationModel.Background;

namespace CapPo.App.Background;

/// <summary>
/// Serves <see cref="NetworkCheckTask"/> over COM when Windows starts CapPo.exe with -RegisterForBGTaskServer (the
/// com:ExeServer in Package.appxmanifest), and keeps the process up until the task is done.
/// </summary>
internal static partial class ComServer
{
    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    private static readonly ManualResetEventSlim s_done = new();

    /// <summary>Runs the COM server until a task completes, or a minute passes without one.</summary>
    /// <remarks>
    /// On an MTA thread: Main's thread is STA, where COM would deliver the call through a message loop that the wait
    /// below doesn't run.
    /// </remarks>
    public static int Run()
    {
        var result = 0;
        var thread = new Thread(() => result = Serve());
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int Serve()
    {
        var clsid = typeof(NetworkCheckTask).GUID;
        var hr = CoRegisterClassObject(clsid, new TaskFactory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out var cookie);
        if (hr != S_OK)
        {
            Log.Info($"Couldn't register the background task's COM class: 0x{hr:X8}");
            return hr;
        }
        try
        {
            // Background tasks get about 30 seconds; a minute covers that and a slow start.
            if (!s_done.Wait(TimeSpan.FromMinutes(1)))
                Log.Info("Background task server: no task came; exiting");
        }
        finally
        {
            CoRevokeClassObject(cookie);
        }
        return 0;
    }

    public static void SignalDone() => s_done.Set();

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid rclsid, IClassFactory pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint dwRegister);

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, in Guid iid, out nint instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [GeneratedComClass]
    internal sealed partial class TaskFactory : IClassFactory
    {
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

        public int CreateInstance(nint outer, in Guid iid, out nint instance)
        {
            instance = 0;
            if (outer != 0)
                return CLASS_E_NOAGGREGATION;
            if (iid != IID_IUnknown && iid != IID_IInspectable && iid != typeof(IBackgroundTask).GUID && iid != typeof(NetworkCheckTask).GUID)
                return E_NOINTERFACE;
            instance = WinRT.MarshalInterface<IBackgroundTask>.FromManaged(new NetworkCheckTask());
            return S_OK;
        }

        public int LockServer(bool fLock) => S_OK;
    }
}
#endif
