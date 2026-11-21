using System.Security.Cryptography;
using RimWise.Integration.Updates;

if (args.Length is not 6 and not 7)
    throw new ArgumentException("Usage: DeltaBuilder <from> <to> <base folder> <target folder> <external setup> <output ZIP> [verify-only|test-staging]");
var (from, to, basis, target, setup, output) = (args[0], args[1], args[2], args[3], args[4], args[5]);
if (args.Length == 6) BinaryUpdatePackage.Create(basis, target, from, to, setup, output);
else if (args[6] is not ("verify-only" or "test-staging")) throw new ArgumentException("Unknown verification mode.");
using var stream = File.OpenRead(output);
var release = new ReleaseDownload(to, Path.GetFileName(output), new Uri("https://github.com/"), stream.Length,
    Convert.ToHexString(SHA256.HashData(stream)), false) { FromVersion = from };
using var prepared = BinaryUpdatePackage.Prepare(output, basis, release, CancellationToken.None);
prepared.VerifyPayload(CancellationToken.None);
Console.WriteLine($"Verified {from} -> {to}: {stream.Length:N0} bytes");
if (args.Length == 7 && args[6] == "test-staging")
{
    Reject(() => BinaryUpdatePackage.Prepare(output, basis, release with { Sha256 = new string('0', 64) }, default));
    Reject(() => BinaryUpdatePackage.Prepare(output, target, release, default));
    var stage = Path.GetDirectoryName(prepared.InstallerPath)!;
    var readme = Path.Combine(stage, "payload", "README.md");
    File.AppendAllText(readme, "\nTampered staging test");
    try { prepared.VerifyPayload(default); throw new Exception("Modified payload accepted."); }
    catch (InvalidDataException) { }
    prepared.RecordHandoff();
    prepared.HandOff();
    File.SetLastWriteTimeUtc(Path.Combine(stage, "rimwise-staged-update.json"), DateTime.UtcNow.AddHours(-1));
    PreparedBinaryUpdate.ReclaimExpired();
    if (File.Exists(prepared.InstallerPath) || !File.Exists(readme) || File.Exists(Path.Combine(stage, "payload", "RimWise.exe")))
        throw new Exception("Staging cleanup did not preserve the modified file or reclaim the known files.");
    File.Delete(readme);
    Directory.Delete(Path.Combine(stage, "payload"));
    Directory.Delete(stage);
    Console.WriteLine("Wrong bytes/source and changed staging refused; expired staging safely reclaimed.");
}

static void Reject(Func<PreparedBinaryUpdate> prepare)
{
    try { using var unexpected = prepare(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
    throw new Exception("Invalid delta was accepted.");
}
