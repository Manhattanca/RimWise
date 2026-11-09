using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal static partial class DiagnosticCodeEvidence
{
    internal const int MaximumMethods = 3;
    internal const int MaximumCodeCharacters = 6000;
    // Only concrete stack frames; namespaces/package names alone never select a method.
    [GeneratedRegex(@"(?:^|\n)\s*(?:at\s+)?([A-Za-z_][\w.`+]*(?::[\w.`]+)?)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Frames();

    internal static async Task<AiLogContext> EnrichAsync(AiLogContext context, ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion version, string cacheRoot, CancellationToken token,
        string? executable = null)
    {
        token.ThrowIfCancellationRequested();
        var symbols = context.Excerpts.SelectMany(e => Frames().Matches(e.Text)
                .Select(m => m.Groups[1].Value.Replace(':', '.'))).Distinct(StringComparer.Ordinal).Take(32).ToArray();
        if (symbols.Length == 0 && context.Owners.Count == 0) return context;
        executable ??= Path.Combine(AppContext.BaseDirectory, "Decompiler", "RimWise.Decompiler.exe");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var evidence = new List<AiCodeEvidence>();
        var status = "available";
        LocalDecompilerClient? client = null;
        try
        {
            // Hashing/metadata parsing must not run on the UI thread.
            var index = await Task.Run(() => LocalAssemblyIndex.Build(catalogue, order, version, cacheRoot, deadline.Token), deadline.Token);
            var related = context.Owners.Select(o => o.PackageId).Concat(index.Assemblies
                .Where(a => a.Metadata.Methods.Any(m => symbols.Contains(m.Symbol, StringComparer.Ordinal)))
                .Select(a => a.PackageId));
            context = context with { Assemblies = LocalCodePreflight.SelectRelated(LocalCodePreflight.Inspect(index, deadline.Token), related) };
            if (symbols.Length == 0) return context.CopyValidated();
            if (!File.Exists(executable)) return (context with { CodeStatus = "unavailable" }).CopyValidated();
            var candidates = new List<(IndexedAssembly Assembly, IndexedMethod Method)>();
            var limited = index.Limited;
            foreach (var symbol in symbols)
            {
                var matches = index.Assemblies.SelectMany(a => a.Metadata.Methods
                    .Where(m => m.Symbol == symbol).Select(m => (Assembly: a, Method: m))).Take(2).ToArray();
                // Overloads and duplicate assembly owners need more evidence; never pick the first.
                if (matches.Length > 1) { limited = true; continue; }
                if (matches.Length == 1) candidates.Add(matches[0]);
            }
            if (candidates.Count == 0) return context with { CodeStatus = limited ? "limited" : "no-matching-method" };
            var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                "decompiler-42c3f910-rimwise-v2|" + version + "|" + string.Join('|', index.Assemblies.Select(a => a.Sha256)))));
            status = limited || candidates.Count > MaximumMethods ? "limited" : "available";
            foreach (var item in candidates.Take(MaximumMethods))
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var path = ScopedPaths.Child(cacheRoot, $"method-v2-{scope}-{item.Assembly.Sha256}-{item.Method.Token:x8}.json");
                    var reply = ReadCache(path, item.Assembly.Sha256);
                    if (reply is null)
                    {
                        if (client is null)
                        {
                            client = new(executable);
                            await client.InitializeAsync(index.Assemblies, deadline.Token);
                        }
                        var response = await client.DecompileAsync(item.Assembly.Id, item.Method.Token, deadline.Token);
                        var text = response.GetProperty("content").EnumerateArray()
                            .First(e => e.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
                        reply = JsonSerializer.Deserialize<CodeReply>(text ?? "null");
                        if (!Valid(reply, item.Assembly.Sha256)) throw new InvalidDataException("Unexpected code response.");
                        reply = reply! with { Code = DiagnosticLogContext.Sanitize(reply!.Code) };
                        // Cache failures do not discard useful evidence.
                        try { LocalAssemblyIndex.WriteCache(path, reply); }
                        catch (Exception ex) when (Recoverable(ex)) { status = "limited"; }
                    }
                    var code = DiagnosticLogContext.Sanitize(reply.Code);
                    if (string.IsNullOrWhiteSpace(code)) { status = "limited"; continue; }
                    var remaining = Math.Min(2048, MaximumCodeCharacters - evidence.Sum(e => e.Text.Length));
                    if (remaining <= 0) { status = "limited"; break; }
                    var truncated = reply.Truncated || code.Length > remaining;
                    if (code.Length > remaining) code = code[..remaining];
                    evidence.Add(new("code-" + evidence.Count, item.Assembly.PackageId, item.Method.Symbol,
                        item.Assembly.Sha256, code, truncated));
                }
                catch (Exception ex) when (Recoverable(ex)) { status = "limited"; }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { status = "timeout"; }
        catch (Exception ex) when (Recoverable(ex)) { status = "unavailable"; }
        finally { if (client is not null) await client.DisposeAsync(); }
        token.ThrowIfCancellationRequested();
        return (context with { Code = evidence, CodeStatus = status }).CopyValidated();
    }

    private static CodeReply? ReadCache(string path, string hash)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return null;
            var reply = JsonSerializer.Deserialize<CodeReply>(File.ReadAllText(path));
            return Valid(reply, hash) ? reply : null;
        }
        catch (Exception ex) when (Recoverable(ex)) { return null; }
    }

    private static bool Valid(CodeReply? reply, string hash) => reply is { Code.Length: <= 4096 } && reply.AssemblyHash == hash;
    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or XmlException
        or InvalidOperationException or System.ComponentModel.Win32Exception or KeyNotFoundException or ArgumentException;
    private sealed record CodeReply(string Code, bool Truncated, string AssemblyHash);
}
