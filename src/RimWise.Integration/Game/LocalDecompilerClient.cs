using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Small sequential MCP stdio client owned by the trusted broker. Timeout/cancellation
/// terminates only its own subprocess. Requests and replies are bounded; stderr is drained.</summary>
internal sealed class LocalDecompilerClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stderr;
    private readonly DecompilerProcessBudget budget;
    private readonly char[] buffer = new char[4096];
    private int buffered, position;
    private int sequence;
    internal LocalDecompilerClient(string executable)
    {
        var start = new ProcessStartInfo(ScopedPaths.Ordinary(executable, true))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        process = Process.Start(start) ?? throw new IOException("Decompiler did not start.");
        try { budget = new(process); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        stderr = Task.Run(async () => { var buffer = new char[1024]; while (await process.StandardError.ReadAsync(buffer) > 0) { } });
    }

    internal async Task InitializeAsync(IReadOnlyList<IndexedAssembly> assemblies, CancellationToken token)
    {
        var initialized = await RequestAsync("initialize", new { protocolVersion = "2024-11-05", capabilities = new { },
            clientInfo = new { name = "RimWise", version = "1" } }, token);
        if (initialized.GetProperty("protocolVersion").GetString() != "2024-11-05")
            throw new InvalidDataException("Unsupported decompiler protocol version.");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        var listed = await RequestAsync("tools/list", new { }, token);
        var names = listed.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray();
        if (!names.SequenceEqual(new[] { "configure", "decompile_method" }))
            throw new InvalidDataException("Unexpected decompiler tools.");
        await CallAsync("configure", new { assemblies = assemblies.Select(a => new { a.Id, a.Path, a.Sha256, a.Metadata.Name }).ToArray() }, token);
    }

    internal Task<JsonElement> DecompileAsync(string id, int methodToken, CancellationToken token) =>
        CallAsync("decompile_method", new { assemblyId = id, metadataToken = methodToken }, token);

    internal async Task<JsonElement> CallAsync(string name, object arguments, CancellationToken token)
    {
        var result = await RequestAsync("tools/call", new { name, arguments }, token);
        if (result.TryGetProperty("isError", out var error) && error.GetBoolean()) throw new InvalidDataException("Decompiler rejected the request.");
        return result;
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var id = ++sequence;
        var line = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException("Decompiler request exceeds limit.");
        await process.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token);
        await process.StandardInput.FlushAsync(timeout.Token);
        while (true)
        {
            var reply = await ReadLineAsync(timeout.Token);
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var received) || !received.TryGetInt32(out var value) || value != id) continue;
            if (root.TryGetProperty("error", out _)) throw new InvalidDataException("Decompiler protocol error.");
            return root.GetProperty("result").Clone();
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        var text = new StringBuilder();
        while (true)
        {
            if (position == buffered)
            {
                buffered = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token);
                position = 0;
                if (buffered == 0) throw new IOException("Decompiler disconnected.");
            }
            var end = Array.IndexOf(buffer, '\n', position, buffered - position);
            var count = (end < 0 ? buffered : end) - position;
            text.Append(buffer, position, count);
            position += count + (end < 0 ? 0 : 1);
            if (text.Length > 128 * 1024) throw new InvalidDataException("Decompiler response exceeds limit.");
            if (end >= 0) return text.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        budget.Dispose();
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException) { }
        process.Dispose();
    }
}
