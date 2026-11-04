namespace RimWise.Integration.Safety;

/// <summary>Application validation, not a substitute for an OS process sandbox.</summary>
public static class ScopedPaths
{
    public static string Ordinary(string path, bool mustExist = false)
    {
        var full = Normalize(path);
        if (mustExist && !Path.Exists(full)) throw new FileNotFoundException("目标不存在。", full);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"目标路径含符号链接或目录联接，已停止操作。目标：{full}；链接位置：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    // Syntax only. Callers must separately verify and pin every filesystem component.
    internal static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\?", StringComparison.Ordinal))
            throw new IOException("需要普通本地绝对路径。");
        ValidateNames(path);
        var full = Path.GetFullPath(path);
        if (full[(Path.GetPathRoot(full)?.Length ?? 0)..].Contains(':'))
            throw new IOException("不允许数据流路径。");
        return full;
    }

    public static string Child(string root, string relative)
    {
        root = Ordinary(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(part => part is ".." or "." || part.Contains(':')))
            throw new IOException("只允许根目录内的相对路径。");
        var result = Ordinary(Path.Combine(root, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("目标超出允许目录。");
        return result;
    }

    private static void ValidateNames(string path)
    {
        // Validate before GetFullPath: Win32 normalization can otherwise erase a
        // trailing dot/space and silently change the name being authorized.
        foreach (var part in path[(Path.GetPathRoot(path)?.Length ?? 0)..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") continue;
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsDevice(stem))
                throw new IOException("路径含 Windows 设备名或不明确的文件名，已停止操作。");
        }
    }

    private static bool IsDevice(string name)
        => name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
        || (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(name[3]));
}
