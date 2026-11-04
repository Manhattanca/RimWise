namespace RimWise.Formats;

public sealed class OrderFileService
{
    public OrderFileDocument Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new OrderFileCodec().Read(input);
    }

    public void ExportNew(string path, OrderFileDocument document, OrderFileFormat format) =>
        WriteNew(path, new OrderFileCodec().Write(document, format));

    internal static void WriteNew(string path, byte[] bytes)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".rim-order-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
