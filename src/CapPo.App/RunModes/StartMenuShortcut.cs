#if !STORE_BUILD
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CapPo.App.RunModes;

/// <summary>
/// The GitHub build's Start menu shortcut. That build has no installer, so CapPo makes the shortcut itself (per user,
/// no administrator rights). The Store build is in Start as an installed app.
/// </summary>
internal static partial class StartMenuShortcut
{
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    private static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppInfo.Name}.lnk");

    /// <summary>
    /// Made on the first start; on later ones pointed at this exe again, in case its folder moved. Once made, a
    /// shortcut the user deleted stays deleted.
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        if (settings.StartMenuShortcutMade && !File.Exists(LinkPath))
            return;
        Write();
        if (!settings.StartMenuShortcutMade)
        {
            settings.StartMenuShortcutMade = true;
            settings.Save();
            Log.Info($"Added to the Start menu: {LinkPath}");
        }
    }

    /// <summary>For --unregister: removes it, and forgets it was made, so a later first start makes it again.</summary>
    public static void Remove()
    {
        if (File.Exists(LinkPath))
            File.Delete(LinkPath);
        var settings = AppSettings.Load();
        if (settings.StartMenuShortcutMade)
        {
            settings.StartMenuShortcutMade = false;
            settings.Save();
        }
    }

    private static void Write()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_ShellLink, 0, CLSCTX_INPROC_SERVER, typeof(IShellLinkW).GUID, out var pointer));
        try
        {
            var link = (IShellLinkW)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            link.SetPath(AppInfo.ExePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(AppInfo.ExePath)!);
            link.SetDescription("Sign in to Wi-Fi portals with encrypted DNS kept on");
            link.SetIconLocation(AppInfo.ExePath, 0);
            ((IPersistFile)link).Save(LinkPath, true);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    // Every method up to SetPath, in vtable order; only the setters are used.
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint file, int max, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription(nint name, int max);
        void SetDescription(string name);
        void GetWorkingDirectory(nint directory, int max);
        void SetWorkingDirectory(string directory);
        void GetArguments(nint arguments, int max);
        void SetArguments(string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(nint path, int max, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load(string fileName, uint mode);
        void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string fileName);
        void GetCurFile(out nint fileName);
    }
}
#endif
