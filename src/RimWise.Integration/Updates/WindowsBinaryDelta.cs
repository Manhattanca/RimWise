using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RimWise.Integration.Updates;

// The Windows SDK msdelta.h ABI. The RAW transform works on DLLs and non-PE resources alike.
internal static class WindowsBinaryDelta
{
    internal const int MaximumFileSize = 128 * 1024 * 1024;
    private const long Raw = 1;

    internal static byte[] Create(byte[] source, byte[] target)
    {
        using var input = new PinnedInput(source);
        using var output = new PinnedInput(target);
        if (!CreateDeltaB(Raw, 0, 0, input.Value, output.Value, default, default, default, 0, 0, out var delta))
            throw new Win32Exception();
        return CopyAndFree(delta, null);
    }

    internal static byte[] Apply(byte[] source, byte[] patch, int expectedLength)
    {
        using var input = new PinnedInput(source);
        using var delta = new PinnedInput(patch);
        if (expectedLength < 0 || expectedLength > MaximumFileSize ||
            !GetDeltaInfoB(delta.Value, out var header) || header.FileTypeSet != Raw || header.FileType != Raw ||
            header.TargetSize != (nuint)expectedLength)
            throw new InvalidDataException("差异补丁头与预期文件不符。");
        if (!ApplyDeltaB(0, input.Value, delta.Value, out var target)) throw new Win32Exception();
        return CopyAndFree(target, expectedLength);
    }

    private static byte[] CopyAndFree(Output value, int? expectedLength)
    {
        try
        {
            if (value.Length > MaximumFileSize || (expectedLength is { } size && value.Length != (nuint)size))
                throw new InvalidDataException("差异处理输出大小不符。");
            var bytes = new byte[(int)value.Length];
            if (bytes.Length > 0) Marshal.Copy(value.Address, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { if (value.Address != 0) DeltaFree(value.Address); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Input(nint Address, nuint Length, int Editable);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Output(nint Address, nuint Length);

    [StructLayout(LayoutKind.Sequential, Size = 80)]
    private struct Header
    {
        public long FileTypeSet;
        public long FileType;
        public long Flags;
        public nuint TargetSize;
    }

    private sealed class PinnedInput : IDisposable
    {
        private GCHandle handle;
        internal PinnedInput(byte[] bytes) => handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        internal Input Value => new(handle.AddrOfPinnedObject(), (nuint)((byte[])handle.Target!).Length, 0);
        public void Dispose() => handle.Free();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msdelta.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateDeltaB(long fileTypes, long setFlags, long resetFlags, Input source, Input target,
        Input sourceOptions, Input targetOptions, Input globalOptions, nint targetTime, uint hashAlgorithm, out Output output);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msdelta.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool GetDeltaInfoB(Input delta, out Header header);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msdelta.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool ApplyDeltaB(long flags, Input source, Input delta, out Output output);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msdelta.dll", ExactSpelling = true)]
    private static extern bool DeltaFree(nint address);
}
