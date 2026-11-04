namespace RimWise.Formats;

/// <summary>
/// The prototype can read selected files and create a new export only.
/// Replacing live configuration requires the later backup/transaction executor.
/// </summary>
public sealed class PrototypeFileService
{
    private readonly ModsConfigXmlFormat format = new();

    public ModsConfigDocument Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return format.Read(input);
    }

    public void ExportNew(string path, ModsConfigDocument document)
    {
        var bytes = format.Write(document);
        // CreateNew is an atomic refusal to replace any existing file, including the input.
        // Failure may leave a partial NEW export; it cannot truncate an existing configuration.
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }
}
