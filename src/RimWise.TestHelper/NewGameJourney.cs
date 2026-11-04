#nullable disable
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace RimWise.TestHelper
{
    // Drives the game's existing new-colony pages. Native page validation and scenario
    // hooks remain authoritative; this code never manufactures a world or settlement.
    internal sealed class NewGameJourney
    {
        private readonly Assembly game;
        private readonly Action worldReady;
        private object lastPage;
        private DateTime pageSeen, advancedAt;
        private bool advanced, worldChecked;
        internal bool MapQueued { get; private set; }
        internal string Stage { get; private set; }

        internal NewGameJourney(Assembly game, Action worldReady)
        {
            this.game = game; this.worldReady = worldReady;
            var page = Activator.CreateInstance(game.GetType("RimWorld.Page_SelectScenario", true));
            var stack = Get(game.GetType("Verse.Find", true), "WindowStack");
            stack.GetType().GetMethod("Add").Invoke(stack, new[] { page });
            // Use an ordinary built-in colony scenario, with its own native config chain.
            var def = Get(game.GetType("RimWorld.ScenarioDefOf", true), "Crashlanded");
            Set(page, "curScen", Get(def, "scenario"));
            Observe(page);
        }

        internal void Step()
        {
            if (MapQueued) return;
            if ((bool)Get(game.GetType("Verse.LongEventHandler", true), "AnyEventNowOrWaiting")) return;
            var find = game.GetType("Verse.Find", true);
            var stack = Get(find, "WindowStack");
            var pageType = game.GetType("RimWorld.Page", true);
            var page = ((IEnumerable)Get(stack, "Windows")).Cast<object>().LastOrDefault(pageType.IsInstanceOfType);
            if (page == null)
                throw new InvalidOperationException("New-game configuration closed before map preparation completed.");
            if (!Object.ReferenceEquals(lastPage, page)) Observe(page);
            // Every page is allowed to render and run its own opening callbacks.
            if (DateTime.UtcNow - pageSeen < TimeSpan.FromSeconds(2)) return;
            if (advanced)
            {
                if (DateTime.UtcNow - advancedAt > TimeSpan.FromSeconds(60))
                    throw new InvalidOperationException("New-game page did not advance: " + page.GetType().FullName);
                return;
            }
            var currentGame = Get(game.GetType("Verse.Current", true), "Game");
            if (!worldChecked && currentGame != null && Get(currentGame, "World") != null)
            {
                worldReady(); worldChecked = true;
            }
            Configure(page);
            var canNext = Method(page.GetType(), "CanDoNext");
            bool allowed = (bool)canNext.Invoke(page, null);
            advanced = true; advancedAt = DateTime.UtcNow;
            // World generation intentionally returns false: its completion opens next.
            if (!allowed)
            {
                if (page.GetType().FullName == "RimWorld.Page_CreateWorldParams") return;
                throw new InvalidOperationException("Native new-game validation rejected: " + page.GetType().FullName);
            }
            bool last = Get(page, "next") == null && Get(page, "nextAct") != null;
            var previousWindows = ((IEnumerable)Get(stack, "Windows")).Cast<object>().ToArray();
            Method(page.GetType(), "DoNext").Invoke(page, null);
            ConfirmNativeNext(page, stack, previousWindows);
            if (last && !((IEnumerable)Get(stack, "Windows")).Cast<object>().Contains(page))
            {
                if (!worldChecked) throw new InvalidOperationException("No world observed before map preparation.");
                // The last native page calls PageUtility.InitGameStart, including both
                // PrepForMapGen and Scenario.PreMapGenerate before loading the Play scene.
                MapQueued = true; Stage = "waiting-map";
            }
        }

        private void ConfirmNativeNext(object page, object stack, object[] previousWindows)
        {
            // Only accept a newly opened native confirmation whose callback belongs to
            // this native page. This excludes arbitrary mod and destructive dialogs.
            string name = page.GetType().FullName;
            if (name != "RimWorld.Page_SelectStartingSite" && name != "RimWorld.Page_ConfigureStartingPawns") return;
            var dialogType = game.GetType("Verse.Dialog_MessageBox", true);
            var dialogs = ((IEnumerable)Get(stack, "Windows")).Cast<object>()
                .Where(w => w.GetType() == dialogType && !previousWindows.Contains(w)).ToArray();
            if (dialogs.Length != 1) return;
            var dialog = dialogs[0];
            var callback = Get(dialog, "buttonAAction") as Action;
            if (callback == null || callback.GetInvocationList().Length != 1 || (bool)Get(dialog, "buttonADestructive")) return;
            var owner = callback.Method.DeclaringType;
            while (owner != null && owner.IsNested) owner = owner.DeclaringType;
            if (owner != page.GetType() || owner.Assembly != game) return;
            if (!Object.Equals(Get(dialog, "acceptAction"), callback)) return;
            Method(dialogType, "OnAcceptKeyPressed").Invoke(dialog, null);
        }

        private void Configure(object page)
        {
            switch (page.GetType().FullName)
            {
                case "RimWorld.Page_SelectStoryteller":
                    var difficulty = Get(game.GetType("RimWorld.DifficultyDefOf", true), "Rough");
                    Set(page, "storyteller", Get(game.GetType("RimWorld.StorytellerDefOf", true), "Cassandra"));
                    Set(page, "difficulty", difficulty);
                    Set(page, "difficultyValues", Activator.CreateInstance(game.GetType("RimWorld.Difficulty", true), new[] { difficulty }));
                    var init = Get(game.GetType("Verse.Find", true), "GameInitData");
                    Set(init, "permadeathChosen", true); Set(init, "permadeath", false);
                    break;
                case "RimWorld.Page_SelectStartingSite":
                    var data = Get(game.GetType("Verse.Find", true), "GameInitData");
                    data.GetType().GetMethod("ChooseRandomStartingTile").Invoke(data, null);
                    Set(Get(game.GetType("Verse.Find", true), "WorldInterface"), "SelectedTile", Get(data, "startingTile"));
                    break;
            }
        }

        private void Observe(object page)
        {
            lastPage = page; pageSeen = DateTime.UtcNow; advanced = false;
            Stage = page.GetType().Name == "Page_SelectScenario" ? "scenario-selection"
                : page.GetType().Name == "Page_SelectStoryteller" ? "storyteller-selection"
                : page.GetType().Name == "Page_CreateWorldParams" ? "world-generation"
                : page.GetType().Name == "Page_SelectStartingSite" ? "starting-site"
                : page.GetType().Name == "Page_ChooseIdeoPreset" ? "ideology-selection"
                : page.GetType().Name == "Page_ConfigureStartingPawns" ? "starting-pawns" : "game-configuration";
        }

        private static MethodInfo Method(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method != null) return method;
            }
            throw new MissingMethodException(name);
        }
        private static MemberInfo Member(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var property = type.GetProperty(name, flags);
                if (property != null) return property;
                var field = type.GetField(name, flags);
                if (field != null) return field;
            }
            throw new MissingMemberException(name);
        }
        private static object Get(object target, string name)
        {
            var member = Member(target as Type ?? target.GetType(), name);
            var instance = target is Type ? null : target;
            var property = member as PropertyInfo;
            return property != null ? property.GetValue(instance, null) : ((FieldInfo)member).GetValue(instance);
        }
        private static void Set(object target, string name, object value)
        {
            var member = Member(target.GetType(), name);
            var property = member as PropertyInfo;
            if (property != null) property.SetValue(target, value, null);
            else ((FieldInfo)member).SetValue(target, value);
        }
    }
}
