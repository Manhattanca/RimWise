#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;

namespace RimWise.TestHelper
{
    // This is independent of map ticks: reaching the menu is not a successful map test.
    [Verse.StaticConstructorOnStartup]
    public static class StartupProbe
    {
        static StartupProbe()
        {
            RecordLogLocations();
            try
            {
                var type = typeof(Verse.Game).Assembly.GetType("Verse.LongEventHandler", true);
                type.GetMethod("ExecuteWhenFinished", new[] { typeof(Action) }).Invoke(null, new object[] { (Action)Ready });
            }
            catch { /* Older games without this hook remain unverified, never falsely ready. */ }
        }

        // Unity assigns a unique crash-report directory at startup. Query the actual
        // runtime before waiting for the menu; older runtimes may not expose this API.
        private static void RecordLogLocations()
        {
            try
            {
                string arg = Environment.GetCommandLineArgs().SingleOrDefault(a => a.StartsWith("-savedatafolder=", StringComparison.OrdinalIgnoreCase));
                if (arg == null) return;
                string data = Path.GetFullPath(arg.Substring("-savedatafolder=".Length));
                string manifest = Path.Combine(data, "workbench-probe.xml");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 128 * 1024
                    || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0) return;
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 128 * 1024 })) doc.Load(reader);
                Guid ignored;
                string nonce = doc.DocumentElement.GetAttribute("nonce");
                if (doc.DocumentElement.Name != "probe" || !Guid.TryParseExact(nonce, "N", out ignored)) return;
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Windows.CrashReporting", false))
                    .FirstOrDefault(t => t != null);
                var property = type == null ? null : type.GetProperty("crashReportFolder", BindingFlags.Public | BindingFlags.Static);
                string crash = property == null ? null : Convert.ToString(property.GetValue(null, null));
                if (String.IsNullOrWhiteSpace(crash)) return;
                var result = new XmlDocument();
                var root = result.CreateElement("logLocations"); result.AppendChild(root);
                root.SetAttribute("nonce", nonce);
                root.SetAttribute("pid", Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                root.SetAttribute("crash", Path.GetFullPath(crash));
                string target = Path.Combine(data, "workbench-log-locations.xml");
                string temporary = target + ".tmp";
                result.Save(temporary); File.Move(temporary, target);
            }
            catch { /* Optional evidence; its absence must not manufacture a gameplay failure. */ }
        }

        private static void Ready()
        {
            string validatedData = null;
            try
            {
                var current = typeof(Verse.Game).Assembly.GetType("Verse.Current", true);
                var state = current.GetProperty("ProgramState", BindingFlags.Public | BindingFlags.Static);
                string phase = Convert.ToString(state == null
                    ? current.GetField("ProgramState", BindingFlags.Public | BindingFlags.Static).GetValue(null)
                    : state.GetValue(null, null));
                if (phase != "Entry" && phase != "Playing") return;
                string arg = Environment.GetCommandLineArgs().SingleOrDefault(a => a.StartsWith("-savedatafolder=", StringComparison.OrdinalIgnoreCase));
                if (arg == null) return;
                string data = Path.GetFullPath(arg.Substring("-savedatafolder=".Length));
                string manifest = Path.Combine(data, "workbench-probe.xml");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 128 * 1024
                    || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0) return;
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 128 * 1024 })) doc.Load(reader);
                Guid ignored;
                string nonce = doc.DocumentElement.GetAttribute("nonce");
                if (doc.DocumentElement.Name != "probe" || !Guid.TryParseExact(nonce, "N", out ignored)) return;
                validatedData = data;
                string target = Path.Combine(data, "workbench-startup.xml");
                if (File.Exists(target)) return;
                // Menu frames must survive Root_Entry clearing Current.Game after startup.
                ProbeComponent.StartMenu();
                var result = new XmlDocument();
                var root = result.CreateElement("startup"); result.AppendChild(root);
                root.SetAttribute("nonce", nonce);
                root.SetAttribute("pid", Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                root.SetAttribute("stage", "startup-ready");
                string temporary = Path.Combine(data, "workbench-startup.tmp");
                result.Save(temporary); File.Move(temporary, target);
            }
            catch (Exception ex)
            {
                if (validatedData != null)
                    try { File.WriteAllText(Path.Combine(validatedData,"workbench-helper-failure.txt"),"ERROR " + ex.ToString()); }
                    catch { }
            }
        }
    }

    // Runs on the game's main thread. Only a fresh test manifest under -savedatafolder activates it.
    // No networking, commands, arbitrary filenames, model output, or game/mod writes.
    public sealed class ProbeComponent : Verse.GameComponent
    {
        private static readonly Assembly game = typeof(Verse.Game).Assembly;
        private static XmlDocument manifest;
        private static string data, nonce;
        private static int firstTick = -1, lastTick = -1;
        private static DateTime mapSeen, lastAdvance, menuSeen;
        private static bool done, subscribed, menuPassed, factionsPassed;
        private static int pawnsTested, pawnTick;
        private static NewGameJourney journey;
        private static object[] testPawns;
        private static string phase = "main-menu", recordedPhase;
        private IDisposable downloadCallback;
        private readonly HashSet<ulong> awaitingDownloads = new HashSet<ulong>();
        private static DateTime lastPoll;
        private static EventInfo frameEvent;
        private static Delegate frameCallback;
        private static object menuRoot;
        private static bool playObserved;
        private static DateTime returnedToMenu;
        public ProbeComponent(Verse.Game ignored) { }

        internal static void StartMenu()
        {
            var current = game.GetType("Verse.Current", true);
            if (Convert.ToString(Read(current, "ProgramState")) != "Entry") return;
            var root = Read(game.GetType("Verse.Find",true),"Root");
            root.GetType().GetMethod("StartCoroutine",new[] { typeof(IEnumerator) })
                .Invoke(root,new object[] { MenuFrames() });
        }

        private static IEnumerator MenuFrames()
        {
            var probe = new ProbeComponent(null);
            // Root_Entry owns the coroutine until Play loads. GameComponent callbacks
            // then continue the same static state. No artificial Game is created at menu.
            while (!done && Convert.ToString(Read(game.GetType("Verse.Current",true),"ProgramState")) == "Entry")
            {
                probe.Poll();
                yield return null;
            }
        }

        public override void GameComponentUpdate() { Poll(); }
        // Modal welcome/log windows can prevent UpdatePlay in some game versions. GUI keeps
        // reporting the disposable test without depending on simulation ticks.
        public override void GameComponentOnGUI() { Poll(); }

        private void Poll()
        {
            if (done) return;
            try
            {
                if (manifest == null && !ReadManifest()) { done = true; return; }
                if (DateTime.UtcNow - lastPoll < TimeSpan.FromMilliseconds(500)) return;
                lastPoll = DateTime.UtcNow;
                if (manifest.DocumentElement.GetAttribute("mode") == "download") { Download(); return; }
                EnsureFrameMonitor();
                // A logged error does not prove that progression failed. Continue gathering
                // evidence through the map/pawn test; only blocked operations end it early.
                if (!menuPassed)
                {
                    if (Convert.ToString(Read(game.GetType("Verse.Current", true), "ProgramState")) != "Entry")
                    { Write("helper-failed",0,0,1); return; }
                    Progress("main-menu", 0, 0);
                    if (menuSeen == default(DateTime)) menuSeen = DateTime.UtcNow;
                    // Give menu drawing and mod entry callbacks multiple frames before proceeding.
                    if (DateTime.UtcNow-menuSeen < TimeSpan.FromSeconds(3)) return;
                    menuPassed = true;
                }
                if (journey == null)
                {
                    journey = new NewGameJourney(game, WorldReady);
                    Progress(journey.Stage,0,0); return;
                }
                if (!journey.MapQueued)
                {
                    journey.Step();
                    Progress(journey.Stage,0,0); return;
                }
                // Returning to Entry can destroy Current.Game while this application
                // callback survives. Game-owned Find getters (notably TickManager)
                // then throw; inspect the scene failure before accessing them.
                var currentGame = Read(game.GetType("Verse.Current", true), "Game");
                var find = game.GetType("Verse.Find", true);
                var map = currentGame == null ? null : Read(find, "CurrentMap");
                if (map == null)
                {
                    Progress("waiting-map", 0, 0);
                    if (MapGenerationFailed()) Write("map-failed", 0, 0, Math.Max(1, ErrorCount()));
                    return;
                }
                var manager = Read(find, "TickManager");
                if (manager == null) { Progress("waiting-map", 0, 0); return; }
                if (mapSeen == default(DateTime)) mapSeen = lastAdvance = DateTime.UtcNow;
                // First-run mod dialogs otherwise force-pause a quick-test forever. Keep every
                // dialog and log visible; only release its pause flag in this manifest-bound map.
                ReleaseTestDialogPauses();
                // Quick tests can begin paused. Only this disposable test's simulation is advanced.
                var speed = manager.GetType().GetProperty("CurTimeSpeed");
                if (speed != null && speed.CanWrite && Convert.ToInt32(speed.GetValue(manager, null)) == 0)
                    speed.SetValue(manager, Enum.ToObject(speed.PropertyType, 1), null);
                int tick = Convert.ToInt32(Read(manager, "TicksGame"));
                if (firstTick < 0) firstTick = tick;
                if (lastTick != tick) { lastTick = tick; lastAdvance = DateTime.UtcNow; }
                var pawns = Read(map, "mapPawns");
                int colonists = Convert.ToInt32(Read(pawns, "FreeColonistsSpawnedCount"));
                phase = testPawns == null || pawnsTested < testPawns.Length ? "pawn-test" : "map-running";
                Progress(phase, Math.Max(0, tick-firstTick), colonists);
                // Check before TestPawns: a frozen simulation also prevents its next selection.
                if (DateTime.UtcNow-lastAdvance > TimeSpan.FromSeconds(60)
                    || colonists < 1 && DateTime.UtcNow-mapSeen > TimeSpan.FromSeconds(60))
                { Write("map-stalled", Math.Max(0, tick-firstTick), colonists, ErrorCount()); return; }
                if (colonists > 0 && !TestPawns(map,tick)) return;
                if (tick - firstTick < 120 || colonists < 1)
                {
                    return;
                }
                var actual = ((IEnumerable)Read(game.GetType("Verse.LoadedModManager", true), "RunningModsListForReading"))
                    .Cast<object>().Select(m => Convert.ToString(Read(m, "PackageId"))).ToArray();
                var expected = manifest.SelectNodes("/probe/order/li").Cast<XmlNode>().Select(n => n.InnerText).ToArray();
                if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)) { Write("order-mismatch", tick-firstTick, colonists, 1); return; }
                // A failed faction validation is retained while map/pawn coverage continues.
                // It must still prevent a completed map from becoming a clean pass.
                if (!factionsPassed)
                { phase = "faction-check"; Write("faction-failed", tick-firstTick, colonists, Math.Max(1, ErrorCount())); return; }
                Write("map-ready", tick-firstTick, colonists, ErrorCount());
            }
            catch (Exception ex)
            {
                try
                {
                    if (data != null)
                    {
                        // Full details stay in the attempt directory. The broker projects only
                        // allowlisted technical evidence before sending anything to an AI.
                        File.WriteAllText(Path.Combine(data, "workbench-helper-failure.txt"),
                            "ERROR " + ex.ToString());
                        Write("helper-failed", 0, 0, 1);
                    }
                }
                catch { }
                done = true;
                StopFrameMonitor();
            }
        }

        // Root_Entry coroutines and GameComponents are both destroyed when the game's
        // own map exception handler returns to Entry. This main-thread event belongs to
        // the application, so the failure dialog remains observable across that return.
        // Reflection keeps older game runtimes without this event loadable.
        private void EnsureFrameMonitor()
        {
            if (frameCallback != null) return;
            var application = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.Application", false)).FirstOrDefault(t => t != null);
            var hook = application == null ? null : application.GetEvent("onBeforeRender", BindingFlags.Public | BindingFlags.Static);
            if (hook == null) return;
            var callback = Delegate.CreateDelegate(hook.EventHandlerType, this,
                GetType().GetMethod("Poll", BindingFlags.NonPublic | BindingFlags.Instance));
            hook.AddEventHandler(null, callback);
            frameEvent = hook; frameCallback = callback;
        }

        private static void StopFrameMonitor()
        {
            if (frameCallback == null) return;
            frameEvent.RemoveEventHandler(null, frameCallback);
            frameCallback = null; frameEvent = null;
        }

        private static bool MapGenerationFailed()
        {
            var find = game.GetType("Verse.Find", true);
            var root = Read(find, "Root");
            string state = Convert.ToString(Read(game.GetType("Verse.Current", true), "ProgramState"));
            if (state == "Playing" || root != null && root.GetType().FullName == "Verse.Root_Play") playObserved = true;
            // Match the game's translated generation-error title, not arbitrary log
            // errors or other mod dialogs. An explicit failure dialog blocks this run.
            var translator = game.GetType("Verse.Translator", false);
            var translate = translator == null ? null : translator.GetMethod("TranslateSimple", new[] { typeof(string) })
                ?? (translator == null ? null : translator.GetMethod("Translate", new[] { typeof(string) }));
            if (translate != null)
            {
                string title = Convert.ToString(translate.Invoke(null, new object[] { "ErrorWhileGeneratingMapTitle" }));
                var dialog = game.GetType("Verse.Dialog_MessageBox", false);
                var stack = Read(find, "WindowStack");
                if (!String.IsNullOrEmpty(title) && dialog != null && stack != null)
                    foreach (object window in (IEnumerable)Read(stack, "Windows"))
                        if (dialog.IsInstanceOfType(window) && String.Equals(Convert.ToString(Read(window, "title")), title, StringComparison.Ordinal))
                            return true;
            }
            // Some versions dismiss the error window immediately. Require an observed
            // Play scene followed by a *new* Entry root with no pending work or Game.
            // A queue gap before Play starts is not a failed map generation.
            bool returned = playObserved && state == "Entry" && !Object.ReferenceEquals(root, menuRoot)
                && Read(game.GetType("Verse.Current", true), "Game") == null
                && !(bool)Read(game.GetType("Verse.LongEventHandler", true), "AnyEventNowOrWaiting");
            if (!returned) { returnedToMenu = default(DateTime); return false; }
            if (returnedToMenu == default(DateTime)) returnedToMenu = DateTime.UtcNow;
            return DateTime.UtcNow - returnedToMenu >= TimeSpan.FromSeconds(2);
        }

        private int ErrorCount()
        {
            return ((IEnumerable)Read(game.GetType("Verse.Log",true),"Messages")).Cast<object>()
                .Count(m => Convert.ToString(Read(m,"type")) == "Error");
        }

        private void WorldReady()
        {
            phase = "faction-check"; Progress(phase,0,0);
            factionsPassed = CheckFactions();
            menuRoot = Read(game.GetType("Verse.Find", true), "Root");
        }

        private bool CheckFactions()
        {
            var manager = Read(game.GetType("Verse.Find",true),"FactionManager");
            var factions = ((IEnumerable)Read(manager,"AllFactionsListForReading")).Cast<object>().ToArray();
            if (factions.Length==0 || Read(manager,"OfPlayer")==null) return FactionFailure("MissingPlayerOrFactions");
            var ids = new HashSet<int>();
            foreach (var faction in factions)
                if (Read(faction,"def")==null || !ids.Add(Convert.ToInt32(Read(faction,"loadID")))) return FactionFailure("InvalidOrDuplicateFaction");
            var world = Read(game.GetType("Verse.Find",true),"World");
            // A normal new-game world uses the player's configured faction list. In
            // newer games requiredCountAtGameStart is the fallback only when that list
            // is null; demanding every Def here would reject intentionally unselected factions.
            var configured = TryRead(TryRead(world,"info"),"factions") as IEnumerable;
            if (configured != null)
            {
                var selected = configured.Cast<object>().Where(CanExistOnSurface).ToArray();
                foreach (var def in selected.Distinct())
                    if (factions.Count(f => RepresentsFaction(Read(f,"def"),def)) < selected.Count(d => Object.ReferenceEquals(d,def)))
                        return FactionFailure("MissingConfiguredFaction " + Convert.ToString(Read(def,"defName")));
            }
            else
            {
                var factionDef = game.GetType("RimWorld.FactionDef",true);
                var required = factionDef.GetField("requiredCountAtGameStart");
                if (required != null)
                {
                    var database = game.GetType("Verse.DefDatabase`1",true).MakeGenericType(factionDef);
                    foreach (object def in (IEnumerable)Read(database,"AllDefsListForReading"))
                        if (CanExistOnSurface(def) && factions.Count(f => RepresentsFaction(Read(f,"def"),def)) < Convert.ToInt32(required.GetValue(def)))
                            return FactionFailure("MissingRequiredFaction " + Convert.ToString(Read(def,"defName")));
                }
            }
            var objects = Read(world,"worldObjects");
            foreach (var settlement in ((IEnumerable)Read(objects,"Settlements")).Cast<object>())
                if (!factions.Contains(Read(settlement,"Faction"))) return FactionFailure("SettlementHasUnknownFaction");
            return true;
        }

        private static bool CanExistOnSurface(object def)
        {
            var generator = game.GetType("RimWorld.FactionGenerator", false);
            var method = generator == null ? null : generator.GetMethod("CanExistOnLayer", BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) return true; // Older worlds had no layer filtering.
            var grid = Read(game.GetType("Verse.Find",true),"WorldGrid");
            return (bool)method.Invoke(null,new[] { Read(grid,"Surface"), def });
        }

        private static object TryRead(object target, string name)
        {
            if (target == null) return null;
            try { return Read(target,name); }
            catch (MissingMemberException) { return null; }
        }

        private static bool RepresentsFaction(object actual, object required)
        {
            // DLC and mods can intentionally replace a base faction. A generated replacement
            // satisfies that base requirement; demanding both would reject a healthy world.
            var seen = new HashSet<object>();
            while (actual != null && seen.Add(actual))
            {
                if (ReferenceEquals(actual,required)) return true;
                var replacement = actual.GetType().GetField("replacesFaction");
                actual = replacement == null ? null : replacement.GetValue(actual);
            }
            return false;
        }

        private bool FactionFailure(string reason)
        {
            // Local detail for a failed probe, never an arbitrary file exposed to the AI.
            File.WriteAllText(Path.Combine(data,"workbench-factions.txt"),reason);
            return false;
        }

        private bool TestPawns(object map, int tick)
        {
            if (testPawns == null)
            {
                var candidates = ((IEnumerable)Read(Read(map,"mapPawns"),"FreeColonistsSpawned")).Cast<object>().ToList();
                // Local randomness cannot disturb the game's global generation RNG.
                var random = new Random();
                testPawns = candidates.OrderBy(p => random.Next()).Take(3).ToArray();
                if (testPawns.Length==0) return false;
                pawnTick = tick-30;
            }
            if (pawnsTested >= testPawns.Length) return true;
            if (tick-pawnTick<30) return false;
            object pawn=testPawns[pawnsTested];
            var selector=Read(game.GetType("Verse.Find",true),"Selector");
            selector.GetType().GetMethod("ClearSelection").Invoke(selector,null);
            selector.GetType().GetMethod("Select",new[] { typeof(object),typeof(bool),typeof(bool) })
                .Invoke(selector,new object[] { pawn,false,false });
            if (!(bool)selector.GetType().GetMethod("IsSelected").Invoke(selector,new[] {pawn}))
                throw new InvalidOperationException("PawnSelectionFailed");
            // Exercise health, skills and traits, plus several frames of selected-pawn UI.
            if (Read(pawn,"health")==null || Read(pawn,"skills")==null
                || Read(Read(pawn,"story"),"traits")==null) throw new InvalidOperationException("PawnDataMissing");
            pawnsTested++; pawnTick=tick;
            return false;
        }

        private void ReleaseTestDialogPauses()
        {
            var stack = Read(game.GetType("Verse.Find", true), "WindowStack");
            foreach (object window in ((IEnumerable)Read(stack, "Windows")).Cast<object>().ToArray())
            {
                var field = window.GetType().GetField("forcePause", BindingFlags.Instance | BindingFlags.Public);
                if (field != null && field.FieldType == typeof(bool)) field.SetValue(window, false);
            }
        }

        private void Progress(string stage, int ticks, int colonists)
        {
            // A reader, antivirus or indexer can briefly deny replacement on Windows.
            // Heartbeats are advisory: retry on the next frame rather than aborting the
            // game journey. The separate terminal signal still proves test completion.
            phase = stage;
            try { WriteProgress(stage, ticks, colonists); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode != 5 && ex.NativeErrorCode != 32 && ex.NativeErrorCode != 33) throw;
            }
        }

        private void WriteProgress(string stage, int ticks, int colonists)
        {
            phase = stage;
            if (recordedPhase != stage)
            {
                File.AppendAllText(Path.Combine(data,"workbench-stages.log"),DateTime.UtcNow.ToString("O")+" "+stage+Environment.NewLine);
                recordedPhase = stage;
            }
            var doc = new XmlDocument(); var root = doc.CreateElement("progress"); doc.AppendChild(root);
            root.SetAttribute("nonce", nonce); root.SetAttribute("pid", Process.GetCurrentProcess().Id.ToString());
            root.SetAttribute("stage", stage); root.SetAttribute("ticks", ticks.ToString());
            root.SetAttribute("colonists", colonists.ToString());
            string temporary = Path.Combine(data, "workbench-progress.tmp"), target = Path.Combine(data, "workbench-progress.xml");
            doc.Save(temporary);
            // ReplaceFile merges ACLs and can require extra rights in restricted test directories.
            // Rename the staged file instead; readers allow deletion and see a complete XML file.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (!MoveFileEx(temporary, target, 0x9))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            else if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);

        private bool ReadManifest()
        {
            string arg = Environment.GetCommandLineArgs().SingleOrDefault(a => a.StartsWith("-savedatafolder=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return false;
            data = Path.GetFullPath(arg.Substring("-savedatafolder=".Length));
            string path = Path.Combine(data, "workbench-probe.xml");
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            manifest = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 })) manifest.Load(reader);
            nonce = manifest.DocumentElement.GetAttribute("nonce");
            Guid value;
            return manifest.DocumentElement.Name == "probe" && Guid.TryParseExact(nonce, "N", out value)
                && !File.Exists(Path.Combine(data, "workbench-signal.xml"));
        }

        private void Download()
        {
            Assembly steam = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("Steamworks.SteamUGC") != null);
            if (steam == null) { Write("steam-unavailable",0,0,1); return; }
            Type ugc = steam.GetType("Steamworks.SteamUGC", true), idType = steam.GetType("Steamworks.PublishedFileId_t", true);
            var app = steam.GetType("Steamworks.SteamUtils", true).GetMethod("GetAppID").Invoke(null, null);
            if (Convert.ToUInt32(Read(app, "m_AppId")) != 294100 || !(bool)steam.GetType("Steamworks.SteamUser", true).GetMethod("BLoggedOn").Invoke(null,null))
            { Write("steam-unavailable",0,0,1); return; }
            var nodes = manifest.SelectNodes("/probe/downloads/li").Cast<XmlNode>().ToArray();
            if (nodes.Length < 1) throw new InvalidDataException();
            if (downloadCallback == null)
            {
                var resultType = steam.GetType("Steamworks.DownloadItemResult_t", true);
                var callbackType = steam.GetType("Steamworks.Callback`1", true).MakeGenericType(resultType);
                var create = callbackType.GetMethod("Create");
                var method = GetType().GetMethod("DownloadFinished", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(resultType);
                downloadCallback = (IDisposable)create.Invoke(null, new object[] { Delegate.CreateDelegate(create.GetParameters()[0].ParameterType, this, method) });
            }
            bool ready = true;
            foreach (var node in nodes)
            {
                ulong number;
                if (!ulong.TryParse(node.InnerText, out number) || number == 0) throw new InvalidDataException();
                object id = Activator.CreateInstance(idType, new object[] { number });
                uint state = Convert.ToUInt32(ugc.GetMethod("GetItemState").Invoke(null,new[] {id}));
                if (!subscribed)
                {
                    if ((state & 1) == 0) ugc.GetMethod("SubscribeItem").Invoke(null,new[] {id});
                    if ((state & 4) == 0 || (state & 56) != 0)
                    {
                        awaitingDownloads.Add(number);
                        if (!(bool)ugc.GetMethod("DownloadItem").Invoke(null,new object[] {id,true})) throw new InvalidOperationException();
                    }
                }
                // Subscribed + installed, no update/downloading/pending flags. GetItemInstallInfo is
                // checked too; host independently validates the configured workshop directory and package ID.
                if ((state & 5) != 5 || (state & 56) != 0 || awaitingDownloads.Contains(number)) { ready = false; continue; }
                object[] install = { id, (ulong)0, "", (uint)32768, (uint)0 };
                ready &= (bool)ugc.GetMethod("GetItemInstallInfo").Invoke(null, install);
            }
            subscribed = true;
            if (ready) Write("downloads-ready",0,0,0);
        }

        private void DownloadFinished<T>(T result)
        {
            if (done || Convert.ToUInt32(Read(Read(result,"m_unAppID"),"m_AppId")) != 294100) return;
            ulong id = Convert.ToUInt64(Read(Read(result,"m_nPublishedFileId"),"m_PublishedFileId"));
            if (!awaitingDownloads.Contains(id)) return;
            if (Convert.ToInt32(Read(result,"m_eResult")) != 1) { Write("helper-failed",0,0,1); return; }
            awaitingDownloads.Remove(id);
        }

        private void Write(string stage, int ticks, int colonists, int errors)
        {
            int warnings = 0;
            try
            {
                var messages = ((IEnumerable)Read(game.GetType("Verse.Log", true), "Messages")).Cast<object>().ToArray();
                warnings = messages.Count(m => Convert.ToString(Read(m, "type")) == "Warning");
                // Preserve bounded severity-bearing messages even when the runtime ignores logFile.
                using (var writer = new StreamWriter(Path.Combine(data, "workbench-messages.log"), false, System.Text.Encoding.UTF8))
                    foreach (var message in messages.Where(m => Convert.ToString(Read(m, "type")) != "Message").Take(1000))
                    {
                        string text = Convert.ToString(Read(message, "text")) ?? "";
                        writer.WriteLine("2000-01-01 00:00:00.000 " + (Convert.ToString(Read(message, "type")) == "Error" ? "ERROR " : "WARN ")
                            + text.Substring(0, Math.Min(text.Length, 8192)).Replace("\r", "").Replace("\n", "\n "));
                        try
                        {
                            string stack = Convert.ToString(Read(message, "stackTrace")) ?? "";
                            if (stack.Length > 0) writer.WriteLine(" " + stack.Substring(0, Math.Min(stack.Length, 8192))
                                .Replace("\r", "").Replace("\n", "\n "));
                        }
                        catch (MissingMemberException) { /* Some older game versions omit stored stack traces. */ }
                    }
            }
            catch { /* Primary runtime logs remain authoritative; missing fallback never hides failure. */ }
            var result = new XmlDocument();
            var root = result.CreateElement("signal"); result.AppendChild(root);
            root.SetAttribute("nonce", nonce);
            root.SetAttribute("pid", Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            root.SetAttribute("stage", stage); root.SetAttribute("ticks", ticks.ToString());
            root.SetAttribute("colonists", colonists.ToString()); root.SetAttribute("errors", errors.ToString());
            root.SetAttribute("warnings", warnings.ToString());
            root.SetAttribute("phase",phase);
            root.SetAttribute("menu",menuPassed ? "1" : "0");
            root.SetAttribute("factions",factionsPassed ? "1" : "0");
            root.SetAttribute("pawns",pawnsTested.ToString());
            string temporary = Path.Combine(data, "workbench-signal.tmp");
            result.Save(temporary);
            File.Move(temporary, Path.Combine(data, "workbench-signal.xml")); done = true;
            StopFrameMonitor();
            if (downloadCallback != null) { downloadCallback.Dispose(); downloadCallback = null; }
        }

        private static object Read(object target, string name)
        {
            if (target == null) throw new MissingMemberException(name);
            var type = target as Type ?? target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var property = type.GetProperty(name,flags);
            if (property != null) return property.GetValue(target is Type ? null : target,null);
            var field = type.GetField(name,flags);
            if (field == null) throw new MissingMemberException(type.FullName,name);
            return field.GetValue(target is Type ? null : target);
        }
    }
}
