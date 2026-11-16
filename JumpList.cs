using System.Runtime.InteropServices;

namespace Any2Hevc;

/// <summary>
/// The menu shown when right-clicking the app's taskbar button (also when it's pinned and not running). Each task
/// starts this exe with a command; the running copy carries it out (see Program).
/// </summary>
static class JumpList
{
    public sealed record Item(string Title, string Command, string Description);

    /// <summary>Replaces the tasks. A null entry is a separator. Failures are ignored: the menu is a convenience.</summary>
    public static void SetTasks(IEnumerable<Item?> tasks)
    {
        try
        {
            var list = (ICustomDestinationList)new CDestinationList();
            var arrayId = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref arrayId, out _);
            var collection = (IObjectCollection)new CEnumerableObjectCollection();
            var exe = Environment.ProcessPath!;
            foreach (var t in tasks)
                collection.AddObject(t is null ? Separator() : Link(exe, t));
            list.AddUserTasks((IObjectArray)collection);
            list.CommitList();
        }
        catch { }
    }

    static IShellLinkW Link(string exe, Item t)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetArguments("--" + t.Command);
        link.SetIconLocation(exe, 0);
        link.SetDescription(t.Description);
        SetProperty((IPropertyStore)link, PKeyTitle, PropVariant.FromString(t.Title));
        return link;
    }

    static IShellLinkW Separator()
    {
        var link = (IShellLinkW)new CShellLink();
        SetProperty((IPropertyStore)link, PKeyIsSeparator, PropVariant.FromBool(true));
        return link;
    }

    static void SetProperty(IPropertyStore store, PropertyKey key, PropVariant value)
    {
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            value.Free();
        }
    }

    static readonly PropertyKey PKeyTitle = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    static readonly PropertyKey PKeyIsSeparator = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 6);

    // ---------- COM declarations ----------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey(Guid fmtid, int pid)
    {
        public Guid FormatId = fmtid;
        public int PropertyId = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] ushort _type;
        [FieldOffset(8)] IntPtr _pointer;
        [FieldOffset(8)] short _bool;

        public static PropVariant FromString(string s) => new() { _type = 31 /* VT_LPWSTR */, _pointer = Marshal.StringToCoTaskMemUni(s) };
        public static PropVariant FromBool(bool b) => new() { _type = 11 /* VT_BOOL */, _bool = (short)(b ? -1 : 0) };

        public void Free()
        {
            if (_type == 31 && _pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6"), ClassInterface(ClassInterfaceType.None)]
    class CDestinationList { }

    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a"), ClassInterface(ClassInterfaceType.None)]
    class CEnumerableObjectCollection { }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    class CShellLink { }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectCollection
    {
        // IObjectArray
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.Interface)] object item);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath(IntPtr file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
