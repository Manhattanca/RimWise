using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed partial class TestHelperMount
{
    // This is separate from the v1 junction manifest: old releases must never mistake
    // an ordinary deployment for a link they can detach.
    private sealed record DirectoryDeployment(int Schema, string GameDirectory, string SessionId,
        string DirectoryIdentity, OwnedFile[] Files);

    private static void ValidateSession(string session)
    {
        var name = Path.GetFileName(session);
        if (!name.StartsWith("game-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[5..], "N", out _))
            throw new IOException("测试会话标识无效，未部署或清理辅助组件。");
    }

    internal static void EnsureNoHelperRemnants(string gameDirectory)
    {
        var mods = ScopedPaths.Child(gameDirectory, "Mods");
        foreach (var path in Directory.EnumerateFileSystemEntries(mods))
        {
            var name = Path.GetFileName(path);
            foreach (var prefix in new[] { "RimWise-Test-", "RimSortWorkbench-Test-" })
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(name[prefix.Length..], "N", out _))
                    throw new IOException($"检测到尚未安全清理的测试辅助目录：{path}。已停止测试，不会调整或移除玩家模组；请先处理辅助组件清理问题。");
        }
    }

    private static void DeployDirectory(OrdinaryGameTestOptions options, string session)
    {
        var path = LinkPath(options, session);
        var bytes = PayloadBytes();
        using var pins = new PinnedDirectorySet();
        pins.PinParent(path);
        pins.PinDirectory(session);
        if (!CreateDirectory(path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "辅助 Mod 目录不能创建，未覆盖现有内容。");
        using var directory = PinnedDirectory.OpenDirectory(path, 0x10081, share: 3);
        var committed = false;
        try
        {
            var record = new DirectoryDeployment(2, ScopedPaths.Ordinary(options.GameDirectory, true),
                Path.GetFileName(session)[5..], Identity(directory),
                PayloadFiles.Select((file, i) => new OwnedFile(file, Convert.ToHexString(SHA256.HashData(bytes[i])))).ToArray());
            var journal = ScopedPaths.Child(session, "helper-deployment.json");
            var pending = ScopedPaths.Child(session, "helper-deployment.json.pending");
            using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, record);
                output.Flush(flushToDisk: true);
            }
            File.Move(pending, journal, overwrite: true);
            committed = true; // Recovery can now authenticate this exact directory.
            WriteState(session, false, "deploying");
            // Metadata last: a partly written assembly must not appear to be a ready mod.
            foreach (var i in new[] { 1, 0 })
            {
                var file = ScopedPaths.Child(path, PayloadFiles[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                // Pin only children: the root is already held with DELETE access.
                using var child = PinnedDirectory.OpenDirectory(Path.GetDirectoryName(file)!, 0x81, share: 3);
                using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(bytes[i]);
                output.Flush(flushToDisk: true);
            }
            WriteState(session, false, "deployed-ordinary-directory");
        }
        catch
        {
            // Before the journal commits we have written no payload. Remove only this
            // still-held empty object; never delete an existing directory by its name.
            if (!committed)
            {
                try { DeleteByHandle(directory); }
                catch (Win32Exception) { /* Original failure takes precedence. */ }
            }
            throw;
        }
    }

    private static DirectoryDeployment ReadDeployment(string gameDirectory, string session)
    {
        using var input = File.OpenRead(ScopedPaths.Child(session, "helper-deployment.json"));
        if (input.Length is 0 or > 16384) throw new InvalidDataException("辅助组件部署记录大小无效。");
        var record = JsonSerializer.Deserialize<DirectoryDeployment>(input, new JsonSerializerOptions { MaxDepth = 8 });
        if (record is not { Schema: 2, Files.Length: 2 } || record.Files.Any(f => f is null)
            || !string.Equals(record.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase)
            || record.SessionId != Path.GetFileName(session)[5..]
            || record.DirectoryIdentity is null || record.DirectoryIdentity.Length != 40
            || !record.DirectoryIdentity.All(Uri.IsHexDigit)
            || !record.Files.Select(f => f.Path).SequenceEqual(PayloadFiles, StringComparer.Ordinal)
            || record.Files.Any(f => f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("辅助组件部署归属无效，未删除内容。");
        return record;
    }
}
