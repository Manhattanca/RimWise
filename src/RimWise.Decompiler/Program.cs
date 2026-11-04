using System.ComponentModel;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DecompilerServer.Services;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RimWise.Decompiler;

internal static class Program
{
    public static async Task Main()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ApprovedDecompilerTools>();
        await builder.Build().RunAsync();
    }
}

/// <summary>Only the local broker calls this process. No remote model receives these tools.</summary>
[McpServerToolType]
public sealed class ApprovedDecompilerTools
{
    private static readonly object Gate = new();
    private static bool configured;

    [McpServerTool(Name = "configure", ReadOnly = true), Description("Pin this session's read-only assembly allowlist once.")]
    public static string Configure(ApprovedAssembly[] assemblies)
    {
        lock (Gate)
        {
            if (configured) throw new InvalidOperationException("Session already configured.");
            AssemblyReadPolicy.Configure(assemblies);
            configured = true;
            return "ready";
        }
    }

    [McpServerTool(Name = "decompile_method", ReadOnly = true), Description("Read a metadata-token method from an approved assembly ID. No path or source lookup.")]
    public static string DecompileMethod(string assemblyId, int metadataToken)
    {
        lock (Gate)
        {
            if (!configured || (metadataToken & unchecked((int)0xff000000)) != 0x06000000)
                throw new ArgumentException("Expected an approved method definition.");
            var approved = AssemblyReadPolicy.Find(assemblyId);
            using var file = AssemblyReadPolicy.Open(approved.Path);
            var handle = MetadataTokens.EntityHandle(metadataToken);
            if (handle.Kind != HandleKind.MethodDefinition || (metadataToken & 0xffffff) == 0
                || (metadataToken & 0xffffff) > file.Metadata.MethodDefinitions.Count)
                throw new ArgumentException("Method token is outside this assembly.");
            using var resolver = new OwnedAssemblyResolver(approved.Path);
            // No debug information provider, source lookup, project export or assembly execution.
            var decompiler = new CSharpDecompiler(file, resolver, new DecompilerSettings
            { ThrowOnAssemblyResolveErrors = false, ShowXmlDocumentation = false });
            var code = decompiler.DecompileAsString(handle);
            const int maximum = 4096;
            return JsonSerializer.Serialize(new { Code = code.Length > maximum ? code[..maximum] : code,
                Truncated = code.Length > maximum, AssemblyHash = approved.Sha256 });
        }
    }
}
