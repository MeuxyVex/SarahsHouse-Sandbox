using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.XrefScans;
using G = Il2Cpp;

[assembly: MelonInfo(typeof(SandboxHouse.SandboxMod), "Sandbox House", "0.13.1", "vexio")]
[assembly: MelonGame("AceStudio", "SarahsHouse")]

namespace SandboxHouse
{
    public class SandboxMod : MelonMod
    {
        internal static SandboxMod I;

        // ---- Sandbox state -------------------------------------------------
        public static bool Active;
        public static bool BlockQuests = true;
        public static bool BlockDialogs = true;
        public static bool BlockCutscenes = true;
        public static bool FreezeTime = true;
        public static bool TraceCalls = true;
        public static bool Bypass;            // lets our own calls through the gates
        internal static bool NeedUnstuck;

        static bool pendingFromMenu;
        static float readySince = -1f;

        // ---- UI state ------------------------------------------------------
        bool show;
        string currentScene = "";
        string status = "";
        bool xrefDone;
        float saveListAge = 99f;
        readonly List<KeyValuePair<int, string>> saveList = new List<KeyValuePair<int, string>>();
        float y;
        Rect panel;
        int tab;
        G.Character selected;
        int animPage;
        public static bool LoopAnims = true;
        public static bool UnlockGallery = true;
        public static MelonPreferences_Entry<bool> ShowSplash;
        public static MelonPreferences_Entry<string> SplashColor;
        public static MelonPreferences_Entry<float> ButtonOffsetX, ButtonOffsetY;
        public static MelonPreferences_Entry<KeyCode> FreeCamKey;
        public static MelonPreferences_Entry<bool> PortalSet;
        public static MelonPreferences_Entry<float> PortalX, PortalY, PortalZ;
        bool allClips;
        string clipFilter = "";
        List<AnimationClip> clipCache;
        IntPtr clipCacheFor;
        bool clipCacheAll;

        const KeyCode MenuKey = KeyCode.F9;
        const KeyCode PickKey = KeyCode.G;

        public override void OnLateUpdate()
        {
            if (InMainMenu) { MainMenuButton.LateTick(); return; }
            if (Puppets.All.Count > 0)
                try { Puppets.LateTick(); } catch (Exception e) { LoggerInstance.Warning("LateTick: " + e.Message); }
            DuoScene.LateTick();
            FreeCam.LateTick();
            PlayerPose.LateTick();
            try { Eyes.LateTick(); } catch { }
            LoveRoom.ApplyMouse();
        }

        public override void OnInitializeMelon()
        {
            I = this;
            var cat = MelonPreferences.CreateCategory("SandboxHouse", "Sandbox House");
            ShowSplash = cat.CreateEntry("ShowSplash", true, "Show the Modded by Vex screen at startup");
            SplashColor = cat.CreateEntry("SplashColor", "#E58FAE", "Background colour of the Modded by Vex screen (hex)");
            ButtonOffsetX = cat.CreateEntry("SandboxButtonOffsetX", 330f, "Horizontal offset of the Sandbox button from Load");
            ButtonOffsetY = cat.CreateEntry("SandboxButtonOffsetY", 0f, "Vertical offset of the Sandbox button from Load");
            FreeCamKey = cat.CreateEntry("FreeCamKey", KeyCode.F6, "Free camera key");
            PortalSet = cat.CreateEntry("LoveRoomPortalSet", false, "Love Room portal placed in the house");
            PortalX = cat.CreateEntry("LoveRoomPortalX", 0f, "Portal position");
            PortalY = cat.CreateEntry("LoveRoomPortalY", 0f, "Portal position");
            PortalZ = cat.CreateEntry("LoveRoomPortalZ", 0f, "Portal position");
            ApplyPatches();
            SaveInstaller.Run(LoggerInstance);
            LoggerInstance.Msg("Sandbox House ready. F9 = sandbox menu (main menu or in game).");
        }

        // =====================================================================
        // Patches
        // =====================================================================
        void ApplyPatches()
        {
            var gate = new HarmonyMethod(typeof(Gates).GetMethod(nameof(Gates.Gate), BindingFlags.Static | BindingFlags.Public));

            // Story / gameplay systems (blocked while sandbox is active)
            PatchByName(typeof(G.PlayerQuests), gate, "startQuest");
            PatchByName(typeof(G.CutsceneManager), gate, "playCutscene");
            PatchByName(typeof(G.DialogManager), gate,
                "StartDialog", "StartDialogWithMessage", "StartDialogMorphWithParts",
                "StartDialogMorphWithPart", "StartDialogMorphWithPartsDelayed", "StartDialogDelayed_Coroutine");
            PatchByName(typeof(G.GameManager), gate,
                "Autosave", "saveGame", "SaveGameConfirm",                 // saves
                "skipTime", "Sleep", "SleepFromCutscene", "SetTime", "SetTimeFake");  // time
            PatchByName(typeof(G.TimeManager), gate, "skipTime", "skipDay");
            PatchByName(typeof(G.Bed), gate, "OpenSaveMenu", "Autosave");
            try
            {
                var m = typeof(G.Bed).GetMethod("GetDescisions");
                if (m != null) HarmonyInstance.Patch(m, postfix: new HarmonyMethod(typeof(Gates).GetMethod(nameof(Gates.BedOptions))));
            }
            catch (Exception e) { LoggerInstance.Warning("Patch Bed.GetDescisions: " + e.Message); }
            PatchByName(typeof(G.GalleryMenu), new HarmonyMethod(typeof(Gates).GetMethod(nameof(Gates.GalleryUnlocked), BindingFlags.Static | BindingFlags.Public)), "IsSceneUnlocked");
            PatchByName(typeof(G.Door), new HarmonyMethod(typeof(Gates).GetMethod(nameof(Gates.DoorInteract), BindingFlags.Static | BindingFlags.Public)), "interact");

            // Trace only. IMPORTANT: never hook the save-loading code (PersistanceHandler.LoadSave,
            // GameManager.loadGame...): it uses native try/catch, and an il2cpp exception crossing
            // a Harmony trampoline kills the game instantly (that was the v0.1.0/0.1.1 crash).
            PatchByName(typeof(G.MainMenu), gate, "StartNewGame", "StartGame");
            PatchByName(typeof(G.SceneLoader), gate, "loadScene");
            PatchByName(typeof(G.LocationManager), gate, "LoadLocation", "LoadLocationWithoutBlackIn", "HandleLocationChange", "handleLocationChange");
        }

        void PatchByName(Type t, HarmonyMethod prefix, params string[] names)
        {
            foreach (var name in names)
            {
                var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                               .Where(m => m.Name == name).ToList();
                if (methods.Count == 0) { LoggerInstance.Warning($"Method not found: {t.Name}.{name}"); continue; }
                foreach (var m in methods)
                {
                    try { HarmonyInstance.Patch(m, prefix: prefix); }
                    catch (Exception e) { LoggerInstance.Warning($"Patch failed {t.Name}.{name}: {e.Message}"); }
                }
            }
        }

        static string ArgName(object[] args)
        {
            if (args == null || args.Length == 0 || args[0] == null) return "";
            if (args[0] is string s) return s;
            if (args[0] is UnityEngine.Object o) return o == null ? "" : o.name;
            return args[0].ToString();
        }

        internal static bool ShouldBlock(string type, string method, object[] args = null)
        {
            if (!Active || Bypass) return false;
            if (LoveRoom.Running)
            {
                // Love Room loop: keep the chosen cutscene alone, in the Love Room. Everything that would
                // chain to the rest of the story (next cutscene, bus, other location, scene load) is refused.
                if (type == "CutsceneManager" && method == "playCutscene") return !string.Equals(ArgName(args), LoveRoom.Current, StringComparison.OrdinalIgnoreCase);
                if (type == "LocationManager") return !string.Equals(ArgName(args), "loveroom", StringComparison.OrdinalIgnoreCase);
                if (type == "SceneLoader") return true;
                if (type == "PlayerQuests") return true;
                if (type == "Bed") return true;
                if (type == "GameManager") return method == "Autosave" || method == "saveGame" || method == "SaveGameConfirm" || method == "quitToMainMenu" || method == "Sleep" || method == "SleepFromCutscene";
                return false;
            }
            if (LoveScenes.Running)
            {
                // During a two-person scene, let the game run it entirely (dialogs, cutscenes, time),
                // but still never save or advance the story.
                if (type == "PlayerQuests") return BlockQuests;
                if (type == "Bed") return true;
                if (type == "GameManager") return method == "Autosave" || method == "saveGame" || method == "SaveGameConfirm";
                return false;
            }
            switch (type)
            {
                case "PlayerQuests": return BlockQuests && method == "startQuest";
                case "CutsceneManager": return BlockCutscenes && method == "playCutscene";
                case "DialogManager": return BlockDialogs && method.StartsWith("StartDialog");
                case "TimeManager": return FreezeTime;
                case "Bed": return method == "OpenSaveMenu" || method == "Autosave";   // no saving from the bed in sandbox
                case "GameManager":
                    switch (method)
                    {
                        case "Autosave": case "saveGame": case "SaveGameConfirm": return true; // never save from sandbox
                        case "skipTime": case "Sleep": case "SleepFromCutscene": case "SetTime": case "SetTimeFake": return FreezeTime;
                    }
                    return false;
            }
            return false;
        }

        // =====================================================================
        // Main loop
        // =====================================================================
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            currentScene = sceneName;
            LoggerInstance.Msg($"[Scene] loaded: #{buildIndex} '{sceneName}'");
            if (sceneName == "Disclamer") Splash.OnDisclaimerLoaded();
            if (sceneName == "MainMenu")
            {
                if (Active) LoggerInstance.Msg("Sandbox off (back to menu).");
                Active = false;
                Puppets.Forget();
                DuoScene.Forget();
                FreeCam.Forget();
                Clothes.Forget();
                Eyes.Forget();
                LoveRoom.Forget();
                Builder.Forget();
                LoveScenes.Reset();
                Gallery.Reset();
                MainMenuButton.Reset();
                selected = null; clipCache = null;
                saveListAge = 99f;
            }
            else if (sceneName == "SampleScene" && MainMenuButton.SandboxLoad)
            {
                // A save was picked from the "Sandbox" entry of the main menu.
                MainMenuButton.SandboxLoad = false;
                pendingFromMenu = true; readySince = -1f;
                LoggerInstance.Msg("Game started from the Sandbox button: sandbox mode will turn on once the house is ready.");
            }
        }

        bool InMainMenu => currentScene == "MainMenu";

        public override void OnUpdate()
        {
            try
            {
                Splash.Tick();
                if (Input.GetKeyDown(MenuKey)) SetMenu(!show);
                if (!InMainMenu && Input.GetKeyDown(FreeCamKey.Value)) FreeCam.Toggle();
                if (!InMainMenu && G.GameManager.INSTANCE != null && Input.GetKeyDown(KeyCode.F8)) Builder.Toggle();

                if (show) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

                if (!InMainMenu && G.GameManager.INSTANCE != null)
                {
                    if (!show && !Interaction.IsOpen && !DuoScene.Active && !LoveRoom.MenuOpen && Input.GetKeyDown(PickKey)) InteractAimed();
                    if (show && Input.GetMouseButtonDown(0))
                    {
                        Vector2 m = Input.mousePosition; m.y = Screen.height - m.y;
                        if (!panel.Contains(m) && !Builder.MouseOver()) TryPick(true);
                    }
                }

                if (pendingFromMenu)
                {
                    if (GameReady())
                    {
                        if (readySince < 0) readySince = Time.unscaledTime;
                        else if (Time.unscaledTime - readySince > 1.5f)
                        {
                            pendingFromMenu = false; readySince = -1f;
                            Activate();
                        }
                    }
                    else readySince = -1f;
                }

                if (NeedUnstuck && !show && !Interaction.IsOpen)
                {
                    NeedUnstuck = false;
                    Unstuck();
                }

                if (InMainMenu) { Gallery.Tick(); MainMenuButton.Tick(); }
                if (!InMainMenu && G.GameManager.INSTANCE != null)
                {
                    DuoScene.Tick();
                    Builder.Tick();
                    LoveRoom.Tick();
                    LoveRoom.MenuTick();
                    if (!DuoScene.Active && !LoveRoom.MenuOpen) Interaction.Tick();
                    if (Active) { Doors.Tick(); Clothes.Tick(); }
                }
                if (FreeCam.Active) FreeCam.Tick(show || Interaction.IsOpen || DuoScene.Active || Builder.Open);
                saveListAge += Time.unscaledDeltaTime;
            }
            catch (Exception e) { LoggerInstance.Error("OnUpdate: " + e); }
        }

        static bool GameReady()
        {
            var gm = G.GameManager.INSTANCE;
            var lm = G.LocationManager.instance;
            var pm = G.PlayerManager.instance;
            if (gm == null || lm == null || pm == null) return false;
            if (!lm.LocationLoadDone) return false;
            var cm = UnityEngine.Object.FindObjectOfType<G.CutsceneManager>();
            if (cm != null && cm.IsCutscenePlaying()) return false;
            return true;
        }

        // =====================================================================
        // Sandbox actions
        // =====================================================================
        void Activate()
        {
            Active = true;
            LoggerInstance.Msg("=== SANDBOX ENABLED ===");
            try { var dm = G.DialogManager.INSTANCE; if (dm != null) dm.StopDialogImidiatly(); } catch (Exception e) { LoggerInstance.Warning("StopDialog: " + e.Message); }
            try
            {
                var cm = UnityEngine.Object.FindObjectOfType<G.CutsceneManager>();
                if (cm != null && cm.IsCutscenePlaying()) cm.stopCutscene();
            }
            catch (Exception e) { LoggerInstance.Warning("StopCutscene: " + e.Message); }
            GoHome();
            Unstuck();
            status = "Sandbox on. Saving disabled.";
        }

        void Deactivate()
        {
            Active = false;
            DuoScene.Stop();
            LoveRoom.Stop();
            PlayerPose.End(false);
            Puppets.ReleaseAll();
            LoggerInstance.Msg("=== SANDBOX DISABLED === (reload a save to resume the story cleanly)");
            status = "Sandbox off. Reload a save to resume the story.";
        }

        /// <summary>Back to the house with full control (end of a Love Room replay).</summary>
        public void ReturnHome(bool goHome = true)
        {
            if (goHome) GoHome();
            Unstuck();
            BlockPlayer(false);
        }

        void GoHome()
        {
            try
            {
                var lm = G.LocationManager.instance;
                if (lm == null) return;
                string cur = lm.CurrentScene;
                LoggerInstance.Msg($"Current location: '{cur}'");
                if (cur != "home")
                {
                    Bypass = true;
                    try { lm.LoadLocation("home", true); }
                    finally { Bypass = false; }
                    LoggerInstance.Msg("Loading location 'home'.");
                }
            }
            catch (Exception e) { LoggerInstance.Error("GoHome: " + e); }
        }

        void SetTimeSafe(G.TimeManager.Time t)
        {
            try
            {
                Bypass = true;
                G.GameManager.INSTANCE.SetTime(t, true);
                LoggerInstance.Msg($"Time set: {t}");
            }
            catch (Exception e) { LoggerInstance.Error("SetTime: " + e); }
            finally { Bypass = false; }
        }

        void Unstuck()
        {
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            try
            {
                pm.setPlayerInDialog(false);
                pm.setInGui(false);
                pm.setInputAllowed(true);
                pm.allowMovement(true);
                pm.SetCameraRotationBlocked(false);
                pm.SetControllerEnabled(true);
                pm.canInteract = true;
            }
            catch (Exception e) { LoggerInstance.Warning("Unstuck: " + e.Message); }
        }

        void Teleport(G.Character c)
        {
            try
            {
                var pm = G.PlayerManager.instance;
                if (pm == null || c == null) return;
                var cc = pm.playerController;
                Transform tr = cc != null ? cc.transform : pm.transform;
                Vector3 target = c.transform.position + c.transform.forward * 1.3f;
                if (cc != null) cc.enabled = false;
                tr.position = target;
                Vector3 look = c.transform.position; look.y = tr.position.y;
                tr.LookAt(look);
                if (cc != null) cc.enabled = true;
                LoggerInstance.Msg($"Teleported next to {c.name}");
            }
            catch (Exception e) { LoggerInstance.Error("Teleport: " + e); }
        }

        void StartFromSave(int slot)
        {
            try
            {
                var mm = UnityEngine.Object.FindObjectOfType<G.MainMenu>();
                if (mm == null) { status = "MainMenu not found."; return; }
                pendingFromMenu = true;
                readySince = -1f;
                LoggerInstance.Msg($"Starting sandbox from slot {slot}");
                SetMenu(false);
                mm.loadGameConfirm(slot);
            }
            catch (Exception e) { pendingFromMenu = false; LoggerInstance.Error("StartFromSave: " + e); }
        }

        void RefreshSaves()
        {
            saveListAge = 0f;
            saveList.Clear();
            try
            {
                var dict = G.PersistanceHandler.saveFiles;
                if (dict == null) return;
                foreach (var kv in dict)
                {
                    var sd = kv.Value;
                    string label = sd == null ? $"Slot {kv.Key}"
                        : $"Slot {kv.Key} | jour {sd.dayCount} | {sd.location} | {(G.TimeManager.Time)sd.time}";
                    saveList.Add(new KeyValuePair<int, string>(kv.Key, label));
                }
                saveList.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            catch (Exception e) { LoggerInstance.Warning("RefreshSaves: " + e.Message); }
        }

        // =====================================================================
        // Menu (IMGUI, absolute rects only)
        // =====================================================================
        void SetMenu(bool open)
        {
            show = open;
            var pm = G.PlayerManager.instance;
            if (InMainMenu || pm == null) return;
            try
            {
                pm.setInputAllowed(!open && !FreeCam.Active && !DuoScene.Active);
                pm.SetCameraRotationBlocked(open || FreeCam.Active || DuoScene.Active);
                if (!open) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            }
            catch (Exception e) { LoggerInstance.Warning("SetMenu: " + e.Message); }
        }

        Rect Row(float h = 26f) { var r = new Rect(panel.x + 10, y, panel.width - 20, h); y += h + 4; return r; }
        void Label(string t, float h = 22f) => GUI.Label(Row(h), t);
        bool Button(string t) => GUI.Button(Row(), t);
        bool Toggle(bool v, string t) => GUI.Toggle(Row(22f), v, t);
        Rect Cell(int i, int n, float h = 26f)
        {
            float w = (panel.width - 20 - (n - 1) * 4) / n;
            return new Rect(panel.x + 10 + i * (w + 4), y, w, h);
        }

        public override void OnGUI()
        {
            if (Interaction.IsOpen && !InMainMenu) Interaction.Draw();
            if (DuoScene.Active && !InMainMenu) DuoScene.Draw();
            if (FreeCam.Active && !InMainMenu) FreeCam.Draw();
            if (!InMainMenu) Builder.Draw();
            if (!InMainMenu) { LoveRoom.DrawBar(); LoveRoom.DrawMenu(); }
            if (!show) return;
            try
            {
                panel = new Rect(Screen.width - 480, 40, 450, Mathf.Min(Screen.height - 80, 760));
                GUI.Box(panel, ""); GUI.Box(panel, ""); GUI.Box(panel, "Sandbox House — F9");
                y = panel.y + 28;

                if (InMainMenu) DrawMainMenu();
                else if (G.GameManager.INSTANCE != null) DrawInGame();
                else Label("Wait until the game is loaded…");

                if (!string.IsNullOrEmpty(status)) { y += 6; Label(status, 40f); }
            }
            catch (Exception e) { LoggerInstance.Error("OnGUI: " + e); show = false; }
        }

        void DrawMainMenu()
        {
            if (saveListAge > 2f) RefreshSaves();
            Label("Pick a save to open in sandbox mode:");
            Label("(ideally a save where everyone already lives in the house)", 22f);
            if (saveList.Count == 0) Label("No save found.");
            int shown = 0;
            foreach (var s in saveList)
            {
                if (shown++ >= 16) { Label("…"); break; }
                if (Button(s.Value)) StartFromSave(s.Key);
            }
            y += 10;
            UnlockGallery = Toggle(UnlockGallery, " Unlock the whole gallery (reopen the gallery to see it)");
            if (!xrefDone && Button("(Diagnostic) Analyse game functions — may crash"))
            { xrefDone = true; DumpXrefs(); }
        }

        bool showOptions;

        void DrawInGame()
        {
            var lm = G.LocationManager.instance;
            string loc = lm != null ? lm.CurrentScene : "?";

            var top = Row(28f);
            GUI.Label(new Rect(top.x, top.y + 4, top.width - 150, 22), $"Sandbox {(Active ? "ON" : "off")}  ·  {loc}");
            if (GUI.Button(new Rect(top.xMax - 140, top.y, 140, 28), Active ? "Disable" : "Enable"))
            { if (Active) Deactivate(); else Activate(); }
            y += 4;

            if (GUI.Button(Cell(0, 2, 28f), "Everyone here")) Puppets.BringInFront(Puppets.Characters().ToList());
            if (GUI.Button(Cell(1, 2, 28f), "Release everyone")) Puppets.ReleaseAll();
            y += 36;

            CharacterRow(null);
            foreach (var c in Puppets.Characters()) CharacterRow(c);

            y += 8;
            if (Button(showOptions ? "▾ Options" : "▸ Options")) showOptions = !showOptions;
            if (showOptions) DrawOptions();
        }

        /// <summary>One line per character: name · [À moi] [Déshabiller/Rhabiller]. c == null is the player.</summary>
        void CharacterRow(G.Character c)
        {
            var r = Row(26f);
            const float bw = 104f;
            string name = c == null ? "You" : c.name + (Puppets.Get(c) != null ? "  ·  frozen" : "");
            GUI.Label(new Rect(r.x, r.y + 3, r.width - 2 * (bw + 4), 22), name);
            float x = r.xMax - 2 * bw - 4;
            if (c != null && GUI.Button(new Rect(x, r.y, bw, 26), "To me")) Puppets.BringInFront(new List<G.Character> { c }, 1.4f);
            x += bw + 4;
            bool naked = c == null ? Clothes.PlayerNaked : Clothes.IsNaked(c);
            if (GUI.Button(new Rect(x, r.y, bw, 26), naked ? "Dress" : "Undress"))
            { if (c == null) Clothes.TogglePlayer(); else Clothes.Toggle(c); }
        }

        void DrawOptions()
        {
            BlockQuests = GUI.Toggle(Cell(0, 2, 22f), BlockQuests, " Block quests");
            BlockDialogs = GUI.Toggle(Cell(1, 2, 22f), BlockDialogs, " Block dialogs");
            y += 24;
            BlockCutscenes = GUI.Toggle(Cell(0, 2, 22f), BlockCutscenes, " Block cutscenes");
            FreezeTime = GUI.Toggle(Cell(1, 2, 22f), FreezeTime, " Freeze time");
            y += 28;

            var times = new[] { G.TimeManager.Time.Morning, G.TimeManager.Time.Noon, G.TimeManager.Time.Afternoon, G.TimeManager.Time.Evening, G.TimeManager.Time.Night };
            var names = new[] { "Morning", "Noon", "Afternoon", "Evening", "Night" };
            for (int i = 0; i < times.Length; i++)
                if (GUI.Button(Cell(i, times.Length), names[i])) SetTimeSafe(times[i]);
            y += 30;

            if (GUI.Button(Cell(0, 3), "House")) GoHome();
            if (GUI.Button(Cell(1, 3), "Unstuck")) Unstuck();
            if (GUI.Button(Cell(2, 3), "Unlock doors")) { Doors.KeepAllUnlocked = true; status = $"{Doors.UnlockAll()} door(s) unlocked."; }
            y += 30;

            if (Button("Place the Love Room portal here (in front of you)")) { LoveRoom.PlaceHere(); status = "Portal placed: aim at it and press G to replay the opening scene."; }
            TraceCalls = Toggle(TraceCalls, " Detailed log (diagnostic)");
            if (Button("Quit to main menu"))
            {
                Active = false; SetMenu(false);
                Puppets.Forget(); DuoScene.Forget(); FreeCam.Exit();
                try { G.GameManager.INSTANCE.quitToMainMenu(); } catch (Exception e) { LoggerInstance.Error("quitToMainMenu: " + e); }
            }
        }

        /// <summary>Freezes the player while one of our menus is open (or gives control back).</summary>
        public void BlockPlayer(bool block)
        {
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            try
            {
                if (block)
                {
                    pm.setInputAllowed(false);
                    pm.SetCameraRotationBlocked(true);
                    Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                }
                else
                {
                    if (FreeCam.Active || DuoScene.Active) return;   // keep the player frozen
                    Unstuck();
                    if (!show) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
                }
            }
            catch (Exception e) { LoggerInstance.Warning("BlockPlayer: " + e.Message); }
        }

        /// <summary>G key: interact with the character or door under the crosshair.</summary>
        void InteractAimed()
        {
            try
            {
                var pm = G.PlayerManager.instance;
                Camera cam = Camera.main;
                if (cam == null && pm != null) cam = pm.mainCamera;
                if (cam == null) return;
                var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0));
                if (LoveRoom.Aimed(ray)) { LoveRoom.Start("PreIntro"); return; }   // the pink sphere only replays the opening scene
                var c = Puppets.Pick(ray, 6f);
                if (c != null) { Interaction.OpenCharacter(c); return; }
                var d = Doors.Pick(ray);
                if (d != null) { Interaction.OpenDoor(d); return; }
                status = "Nothing in range.";
            }
            catch (Exception e) { LoggerInstance.Warning("InteractAimed: " + e.Message); }
        }

        void TryPick(bool fromMouse)
        {
            try
            {
                var pm = G.PlayerManager.instance;
                Camera cam = Camera.main;
                if (cam == null && pm != null) cam = pm.mainCamera;
                if (cam == null) return;
                Vector3 sp = fromMouse ? Input.mousePosition : new Vector3(Screen.width / 2f, Screen.height / 2f, 0);
                var c = Puppets.Pick(cam.ScreenPointToRay(sp));
                if (c == null) return;
                SetMenu(false);
                Interaction.OpenCharacter(c);
            }
            catch (Exception e) { LoggerInstance.Warning("Pick: " + e.Message); }
        }

        // =====================================================================
        // Xref dump: lists which game methods each method calls (poor man's decompiler)
        // =====================================================================
        void DumpXrefs()
        {
            try
            {
                // Writes method by method (flushed), so if the game crashes we know which method caused it.
                var path = Path.Combine(MelonEnvironment.UserDataDirectory, "SandboxHouse_xref.txt");
                var types = new[] { typeof(G.MainMenu), typeof(G.GameManager), typeof(G.LocationManager), typeof(G.PersistanceHandler),
                                    typeof(G.SceneLoader), typeof(G.PlayerQuests), typeof(G.CutsceneManager), typeof(G.TimeManager) };
                int count = 0;
                using (var w = new StreamWriter(path, false) { AutoFlush = true })
                {
                    foreach (var t in types)
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_") || m.Name.StartsWith(".")) continue;
                            w.WriteLine($"## {t.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                            try
                            {
                                foreach (var x in XrefScanner.XrefScan(m))
                                {
                                    if (x.Type != XrefType.Method) continue;
                                    var r = x.TryResolve();
                                    if (r != null) w.WriteLine($"    -> {r.DeclaringType?.Name}.{r.Name}");
                                }
                            }
                            catch (Exception e) { w.WriteLine("    (scan error: " + e.Message + ")"); }
                            count++;
                        }
                    }
                }
                LoggerInstance.Msg($"Xref dump: {count} methods -> {path}");
            }
            catch (Exception e) { LoggerInstance.Warning("DumpXrefs: " + e); }
        }
    }

    // =========================================================================
    // Generic Harmony prefix: traces the call and blocks it when sandbox needs it
    // =========================================================================
    public static class Gates
    {
        public static bool Gate(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string type = __originalMethod.DeclaringType?.Name ?? "?";
                string method = __originalMethod.Name;
                bool block = SandboxMod.ShouldBlock(type, method, __args);
                if (SandboxMod.TraceCalls)
                    MelonLogger.Msg($"[Trace]{(block ? " BLOCKED" : "")} {type}.{method}({string.Join(", ", (__args ?? new object[0]).Select(Describe))})");
                if (block && type == "DialogManager")
                {
                    G.Character who = null;
                    if (__args != null) foreach (var a in __args) if (a is G.Character c) { who = c; break; }
                    if (who != null && method == "StartDialog") Interaction.RequestCharacter(who);
                    else SandboxMod.NeedUnstuck = true;
                }
                return !block;
            }
            catch { return true; }
        }

        static int galleryCalls;
        public static bool GalleryUnlocked(ref bool __result)
        {
            if (!SandboxMod.UnlockGallery) return true;
            __result = true;
            if (galleryCalls++ == 0) MelonLogger.Msg("[Sandbox House] Gallery: all scenes unlocked.");
            return false;   // skip the original check
        }

        public static void BedOptions(ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<G.DescisionOption> __result)
        {
            try
            {
                if (!SandboxMod.Active || __result == null) return;
                var keep = new List<G.DescisionOption>();
                bool removed = false;
                foreach (var o in __result)
                {
                    string t = "";
                    try { t = o != null ? (o.text ?? "") : ""; } catch { }
                    if (t.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("sauve", StringComparison.OrdinalIgnoreCase) >= 0)
                    { removed = true; continue; }
                    keep.Add(o);
                }
                if (removed)
                {
                    __result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<G.DescisionOption>(keep.ToArray());
                    MelonLogger.Msg("[Sandbox House] Bed save option removed (sandbox).");
                }
            }
            catch (Exception e) { MelonLogger.Warning("BedOptions: " + e.Message); }
        }

        public static bool DoorInteract(G.Door __instance)
        {
            try
            {
                if (!SandboxMod.Active || __instance == null) return true;
                if (__instance.isLocked()) { Interaction.RequestDoor(__instance); return false; }
            }
            catch { }
            return true;
        }

        static string Describe(object o)
        {
            try
            {
                if (o == null) return "null";
                if (o is UnityEngine.Object uo) return uo == null ? "null" : uo.name;
                if (o is string s) return "\"" + s + "\"";
                return o.ToString();
            }
            catch { return "?"; }
        }
    }
}
