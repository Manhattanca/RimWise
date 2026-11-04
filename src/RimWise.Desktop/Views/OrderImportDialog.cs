using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RimWise.Desktop.Views;

/// <summary>Windows common dialog with live navigation when the selected file type changes.</summary>
internal static class OrderImportDialog
{
    internal static readonly string[] Kinds = ["all", "xml", "json", "rml", "rws", "any"];
    private const int Cancelled = unchecked((int)0x800704C7);

    internal static string? Show(Window owner, Func<string, string?> folderForKind, Action<IFileDialog>? ready = null)
    {
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true)!)!;
        uint cookie = 0;
        var advised = false;
        var events = new Events(folderForKind);
        try
        {
            dialog.SetTitle("导入排序或存档");
            dialog.SetOptions(0x40 | 0x800 | 0x1000 | 0x10000000); // Filesystem, existing path/file, no recent-doc entry.
            FilterSpec[] filters = [new("排序或存档 (*.xml;*.json;*.rml;*.rws)", "*.xml;*.json;*.rml;*.rws"),
                new("原版 / RimPy (*.xml)", "*.xml"), new("RimSort (*.json)", "*.json"),
                new("RimCrow (*.rml)", "*.rml"), new("环世界存档 (*.rws)", "*.rws"), new("所有文件 (*.*)", "*.*")];
            dialog.SetFileTypes((uint)filters.Length, filters);
            dialog.SetFileTypeIndex(1);
            var client = new Guid("6B9C6411-07DB-4316-A8D4-7F08F99B5F42");
            dialog.SetClientGuid(ref client);
            dialog.Advise(events, out cookie); advised = true;
            events.OnTypeChange(dialog);
            ready?.Invoke(dialog);
            var result = dialog.Show(new WindowInteropHelper(owner).EnsureHandle());
            if (result == Cancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            try { return FilePath(item); }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally
        {
            if (advised) dialog.Unadvise(cookie);
            Marshal.ReleaseComObject(dialog);
            GC.KeepAlive(events);
        }
    }

    internal static string FilePath(IShellItem item)
    {
        item.GetDisplayName(0x80058000, out var name); // SIGDN_FILESYSPATH, UTF-16.
        try { return Marshal.PtrToStringUni(name)!; }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class Events(Func<string, string?> folderForKind) : IFileDialogEvents
    {
        private uint lastType;
        public int OnTypeChange(IFileDialog dialog)
        {
            try
            {
                dialog.GetFileTypeIndex(out var type);
                if (type == lastType || type < 1 || type > Kinds.Length) return 0;
                lastType = type; // SetFolder can raise more dialog events; do not redirect twice.
                var path = folderForKind(Kinds[type - 1]);
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return 0;
                var iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var folder));
                try { dialog.SetFolder(folder); }
                finally { Marshal.ReleaseComObject(folder); }
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            { /* Optional navigation must not close the picker or prevent manual browsing. */ }
            return 0;
        }
        public int OnFileOk(IFileDialog dialog) => 0;
        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;
        public int OnFolderChange(IFileDialog dialog) => 0;
        public int OnSelectionChange(IFileDialog dialog) => 0;
        public int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response) { response = 0; return 0; }
        public int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response) { response = 0; return 0; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FilterSpec(string name, string spec)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec = spec;
    }

    // Native vtable order must match shobjidl_core.h, including unused methods.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid client);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComVisible(true), Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk(IFileDialog dialog);
        [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);
        [PreserveSig] int OnFolderChange(IFileDialog dialog);
        [PreserveSig] int OnSelectionChange(IFileDialog dialog);
        [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response);
        [PreserveSig] int OnTypeChange(IFileDialog dialog);
        [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr bind, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
