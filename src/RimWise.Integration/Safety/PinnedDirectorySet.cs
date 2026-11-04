using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RimWise.Integration.Safety;

/// <summary>Keeps verified ancestor handles for one batch. Only live directory handles
/// are reused; file identity, ACLs, and effective access are always checked anew.</summary>
internal sealed class PinnedDirectorySet : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    internal int Count => handles.Count;

    // Original-directory ACL edits need a MAXIMUM_ALLOWED handle. Release this
    // subtree before that open so our own delete-sharing guard cannot conflict.
    // Its parent remains pinned; the edit rechecks the exact target's file ID.
    internal void ReleaseDirectoryTree(string directory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var full = ScopedPaths.Normalize(directory);
        var prefix = full.TrimEnd('\\') + "\\";
        foreach (var path in handles.Keys.Where(path => path.Equals(full, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray())
        {
            handles[path].Dispose();
            handles.Remove(path);
        }
    }

    internal void PinDirectory(string directory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var full = ScopedPaths.Normalize(directory);
        if (!string.Equals(full, directory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("批量权限操作需要规范的绝对路径。");
        Pin(full);
    }

    internal void PinParent(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var full = ScopedPaths.Normalize(path);
        if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("批量权限操作需要规范的绝对路径。");
        Pin(Path.GetDirectoryName(full) ?? throw new IOException("目标缺少父目录。"));
    }

    private void Pin(string directory)
    {
        if (handles.ContainsKey(directory)) return;
        var root = Path.GetPathRoot(directory) ?? throw new IOException("目标缺少磁盘根路径。");
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null) Pin(parent);
        else if (GetDriveTypeW(root) is not (2 or 3 or 6))
            throw new IOException("批量权限操作只支持本地磁盘。");
        // The parent remains pinned before this child is opened without following reparses.
        // Attribute-only opens do not participate in read/write/delete sharing checks.
        // Allow directory-content writes, but deny renaming/deleting this directory itself.
        handles.Add(directory, PinnedDirectory.OpenDirectory(directory, 0x81, share: 3));
    }

    public void Dispose()
    {
        foreach (var handle in handles.Values.Reverse()) handle.Dispose();
        handles.Clear();
        disposed = true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
}
