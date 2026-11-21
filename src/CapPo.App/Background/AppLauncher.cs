#if STORE_BUILD
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CapPo.App.Background;

/// <summary>Starts CapPo as an app of its package (with package identity), with arguments, from the background task.</summary>
internal static partial class AppLauncher
{
    private static readonly Guid CLSID_ApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private const uint CLSCTX_ALL = 0x17;
    private const int AO_NONE = 0;

    public static void Launch(string arguments)
    {
        var aumid = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
        var hr = CoCreateInstance(CLSID_ApplicationActivationManager, 0, CLSCTX_ALL, typeof(IApplicationActivationManager).GUID, out var pointer);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var manager = (IApplicationActivationManager)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            manager.ActivateApplication(aumid, arguments, AO_NONE, out var processId);
            Log.Info($"Started {aumid} {arguments} (process {processId})");
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    internal partial interface IApplicationActivationManager
    {
        void ActivateApplication(string appUserModelId, string? arguments, int options, out uint processId);
    }
}
#endif
