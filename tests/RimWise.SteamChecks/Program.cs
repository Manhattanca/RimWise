using System.Text.Json;
using RimWise.Integration.Steam;

if (args.Length == 1 && args[0] == "--emergency-diagnostics-checks")
{
    var results = new List<string>();
    EmergencyDiagnosticsChecks.Run((name, test) => { test(); results.Add(name); });
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length == 1 && args[0] == "--live-game-log-checks")
{
    var results = new List<string>();
    LiveGameLogChecks.Run((name, test) => { test(); results.Add(name); });
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length == 1 && args[0] == "--offline-sorting-checks")
{
    var results = new List<string>();
    OfflineSortingChecks.Run((name, test) => { test(); results.Add(name); });
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length == 1 && args[0] == "--helper-cleanup-checks")
{
    var results = new List<string>();
    TestHelperCleanupChecks.Run((name, test) => { test(); results.Add(name); });
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length >= 1 && args[0] == "--diagnostic-attribution-checks")
{
    var results = new List<string>();
    void Record(string name, Action test) { test(); results.Add(name); }
    DiagnosticAttributionChecks.Run(Record);
    MaintenanceProgressChecks.Run(Record);
    MaintenanceDiagnosisChecks.Run(Record);
    AiContextRetentionChecks.Run(Record);
    AiResponseFormatChecks.Run(Record);
    if (args.Length == 3) DiagnosticAttributionChecks.Replay(args[1], args[2]);
    if (args.Length == 2)
    {
        if (Directory.Exists(args[1])) MaintenanceProgressChecks.ReplayHistory(args[1]);
        else DiagnosticAttributionChecks.ReplayArchivedReport(args[1]);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length == 1 && args[0] == "--lifecycle-probe")
{
    Console.WriteLine("ready");
    Console.ReadLine();
    return;
}

if (args.Length == 1 && args[0] == "--ai-response-checks")
{
    var results = new List<string>();
    AiResponseFormatChecks.Run((name, test) => { test(); results.Add(name); });
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length == 1 && args[0] == "--ai-token-estimation-checks")
{
    var results = new List<string>();
    AiTokenEstimationChecks.Run((name, test) => { test(); results.Add(name); });
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length >= 1 && args[0] == "--save-import-checks")
{
    var results = new List<string>();
    SaveImportChecks.Run((name, test) => { test(); results.Add(name); });
    foreach (var path in args.Skip(1)) SaveImportChecks.Replay(path);
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = results.Count, Checks = results }));
    return;
}

if (args.Length == 2 && args[0] == "--log-volume-probe")
{
    await LargeLogChecks.EmitAsync(args[1]);
    return;
}

if (args.Length == 1 && args[0] is "--insertion-ordering-checks" or "--automatic-preparation-checks")
{
    var results = new List<string>();
    void InsertionCheck(string name, Action test) { test(); results.Add(name); }
    InsertionChecks.Run(InsertionCheck);
    InsertionOrderingChecks.Run(InsertionCheck);
    AutomaticPreparationChecks.Run(InsertionCheck);
    AiContextRetentionChecks.Run(InsertionCheck);
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Checks = results }));
    return;
}

var checks = new List<string>();
string Reply(object[] items, string operation = "search") => JsonSerializer.Serialize(new { Success = true, Operation = operation, Items = items });
object Item(string id = "123", uint app = 294100) => new { Id = id, Title = "中文 模组", ConsumerAppId = app };
void Check(string name, Action action) { action(); checks.Add(name); }
void Reject(string name, string json, int exit = 0) => Check(name, () =>
{
    try { SteamWorkshopClient.ParseSearchResult(json, exit); }
    catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
    throw new Exception(name);
});
Check("Chinese title and numeric ID preserved", () =>
{
    var result = SteamWorkshopClient.ParseSearchResult(Reply([Item()]), 0).Single();
    if (result.Id != "123" || result.Title != "中文 模组") throw new Exception("Result changed");
});
Check("Empty search is a valid no-match result", () =>
{ if (SteamWorkshopClient.ParseSearchResult(Reply([]), 0).Count != 0) throw new Exception("Unexpected match"); });
Reject("Unrelated app rejected", Reply([Item(app: 440)]));
Reject("Duplicate IDs rejected", Reply([Item(), Item()]));
Reject("Non-numeric IDs rejected", Reply([Item("../bad")]));
Reject("Non-canonical IDs rejected", Reply([Item("0123")]));
Reject("Wrong operation rejected", Reply([Item()], "subscribe"));
Reject("Process failure rejected", Reply([Item()]), 1);
Reject("Excessive results rejected", Reply(Enumerable.Range(1,51).Select(i => Item(i.ToString())).ToArray()));
Check("AccessDenied preserved for diagnosis", () =>
{
    try { SteamWorkshopClient.ParseResult("{\"Success\":false,\"Error\":\"Steam: k_EResultAccessDenied\"}", "enqueue", ["123"], 1); }
    catch (IOException ex) when (ex.Message.Contains("AccessDenied")) { return; }
    throw new Exception("Error lost");
});
Check("Unconfirmed subscription is never success", () =>
{
    try { SteamWorkshopClient.ParseResult("{\"Success\":true,\"Operation\":\"enqueue\",\"Items\":[{\"Id\":\"123\",\"Subscribed\":false,\"Installed\":false,\"Directory\":\"\"}]}", "enqueue", ["123"], 0); }
    catch (IOException) { return; }
    throw new Exception("Unconfirmed accepted");
});
DependencyChecks.Run(Check);
OfflineSortingChecks.Run(Check);
DiagnosticSummaryChecks.Run(Check);
GameLifecycleChecks.Run(Check);
TestHelperCleanupChecks.Run(Check);
DuplicateInstallationChecks.Run(Check);
DuplicateCopyChecks.Run(Check);
AiContextRetentionChecks.Run(Check);
AutomaticLogChecks.Run(Check);
MaintenanceDiagnosisChecks.Run(Check);
DiagnosticAttributionChecks.Run(Check);
MaintenanceProgressChecks.Run(Check);
LargeLogChecks.Run(Check);
AiResponseFormatChecks.Run(Check);
GameVersionCompatibilityChecks.Run(Check);
InsertionChecks.Run(Check);
InsertionOrderingChecks.Run(Check);
AutomaticPreparationChecks.Run(Check);
AiConnectionImportChecks.Run(Check);
AiServiceConfigurationChecks.Run(Check);
AiTokenEstimationChecks.Run(Check);
StorageMigrationChecks.Run(Check);
ReleaseUpdateChecks.Run(Check);
SaveImportChecks.Run(Check);
if (args.Length == 2 && args[0] == "--diagnostic-replay") MaintenanceDiagnosisChecks.Replay(args[1]);
if (args.Length > 1 && args[0] == "--metadata-no-dependencies")
    DependencyChecks.CheckOptionalMetadata(args.Skip(1), Check);
if (args.Length == 2 && args[0] == "--live")
{
    var client = new SteamWorkshopClient(args[1]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var results = await client.SearchAsync("RimTalk", timeout.Token);
    if (results.Count == 0 || results.Any(i => i.ConsumerAppId != 294100)) throw new Exception("Live search failed");
    checks.Add($"Live Steam search: {results.Count} RimWorld candidates, no subscription changes");
    var state = await client.QueryAsync("2009463077", timeout.Token);
    if (state.Count != 1) throw new Exception("Live query failed");
    checks.Add("Live known public item query succeeded");
    try { await client.QueryAsync("3598813474", timeout.Token); }
    catch (IOException ex) when (ex.Message.Contains("AccessDenied"))
    { checks.Add("Reported item: Steam AccessDenied reproduced with readable diagnostic"); }
}
Console.WriteLine(JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
