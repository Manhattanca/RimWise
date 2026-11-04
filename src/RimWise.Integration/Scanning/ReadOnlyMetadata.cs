using System.Xml;
using System.Xml.Linq;

namespace RimWise.Integration.Scanning;

internal static class ReadOnlyMetadata
{
    internal const int MaximumBytes = 2 * 1024 * 1024;

    internal static XDocument ReadXml(string file, string boundary)
    {
        using var stream = Open(file, boundary, MaximumBytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes, IgnoreComments = true
        });
        var xml = XDocument.Load(reader);
        if (xml.Descendants().Any(element => element.Ancestors().Take(32).Count() >= 32))
            throw new InvalidDataException("元数据嵌套过深。");
        return xml;
    }

    internal static string ReadText(string file, string boundary, int maximumBytes)
    {
        using var stream = Open(file, boundary, maximumBytes);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        // Also bound reads if Steam or a mod manager extends the file while it is open.
        var buffer = new char[maximumBytes + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > maximumBytes) throw new InvalidDataException("元数据文件过大。");
        return new string(buffer, 0, count).Trim();
    }

    internal static void CheckPath(string path, string boundary)
    {
        path = Path.GetFullPath(path);
        boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
        var prefix = Path.EndsInDirectorySeparator(boundary) ? boundary : boundary + Path.DirectorySeparatorChar;
        if (!path.Equals(boundary, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("路径超出所选目录。");
        for (var current = path; ; current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("该项是目录联接或符号链接，本次未扫描。");
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static FileStream Open(string file, string boundary, int maximumBytes)
    {
        CheckPath(file, boundary);
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= maximumBytes) return stream;
        stream.Dispose();
        throw new InvalidDataException("元数据文件过大。");
    }
}
