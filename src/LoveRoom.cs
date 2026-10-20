using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// "Love Room" portal: an object placed in the house; using it (G) replays one of the game's cutscenes
    /// (the opening one by default) in a loop, without time limit. Stop brings you back to the house.
    /// </summary>
    internal static class LoveRoom
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        // ------------------------------------------------------------------ portal object
        static GameObject portal;
        static float spin;

        static bool HasSpot => SandboxMod.PortalSet.Value || DefaultSpot().HasValue;
        static Vector3 Spot => SandboxMod.PortalSet.Value
            ? new Vector3(SandboxMod.PortalX.Value, SandboxMod.PortalY.Value, SandboxMod.PortalZ.Value)
            : DefaultSpot() ?? Vector3.zero;

        static Vector3? defaultSpot; static float nextDefaultTry;
        /// <summary>Default place: the hallway of the house (found in the loaded "home" location).</summary>
        static Vector3? DefaultSpot()
        {
            if (defaultSpot.HasValue || Time.unscaledTime < nextDefaultTry) return defaultSpot;
            nextDefaultTry = Time.unscaledTime + 2f;
            foreach (var n in new[] { "HallwaySecondEntrance", "HallwayBathToilet", "Stairs" })
            {
                try
                {
                    var go = GameObject.Find(n);
                    if (go == null) continue;
                    Vector3 p = go.transform.position;
                    if (UnityEngine.AI.NavMesh.SamplePosition(p, out UnityEngine.AI.NavMeshHit hit, 3f, UnityEngine.AI.NavMesh.AllAreas)) p = hit.position;
                    defaultSpot = p + Vector3.up * 1.1f;
                    Log.Msg($"Love Room portal: default spot in the hallway ({n}) {defaultSpot}");
                    break;
                }
                catch { }
            }
            return defaultSpot;
        }

        public static void PlaceHere()
        {
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            Transform pt = pm.playerController != null ? pm.playerController.transform : pm.transform;
            Vector3 fwd = pt.forward; fwd.y = 0; fwd.Normalize();
            Vector3 p = pt.position + fwd * 1.2f + Vector3.up * 1.1f;
            SandboxMod.PortalX.Value = p.x; SandboxMod.PortalY.Value = p.y; SandboxMod.PortalZ.Value = p.z;
            SandboxMod.PortalSet.Value = true;
            MelonPreferences.Save();
            DestroyPortal();
            Log.Msg($"Love Room portal placed at {p}");
        }

        static void DestroyPortal()
        {
            try { if (portal != null) UnityEngine.Object.Destroy(portal); } catch { }
            portal = null;
        }

        static void EnsurePortal()
        {
            if (portal != null || !HasSpot) return;
            try
            {
                portal = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                portal.name = "SandboxLoveRoomPortal";
                portal.transform.position = Spot;
                portal.transform.localScale = Vector3.one * 0.28f;
                var col = portal.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);          // never block the player
                var r = portal.GetComponent<Renderer>();
                if (r != null)
                {
                    try
                    {
                        var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                        if (sh != null) r.material = new Material(sh);
                        r.material.color = new Color(1f, 0.35f, 0.65f);
                    }
                    catch { }
                }
            }
            catch (Exception e) { Log.Warning("Portal: " + e.Message); portal = null; }
        }

        /// <summary>Called every frame in game: shows the portal only in the house while idle.</summary>
        public static void Tick()
        {
            string loc = "";
            try { loc = G.LocationManager.instance?.CurrentScene ?? ""; } catch { }
            bool show = SandboxMod.Active && HasSpot && !Running && loc == "home";
            if (!show) { if (portal != null) portal.SetActive(false); }
            else
            {
                EnsurePortal();
                if (portal != null)
                {
                    if (!portal.activeSelf) portal.SetActive(true);
                    spin += Time.deltaTime;
                    float s = 0.28f + Mathf.Sin(spin * 3f) * 0.03f;
                    portal.transform.localScale = new Vector3(s, s, s);
                }
            }
            TickReplay();
            MouseTick();
        }

        // ------------------------------------------------------------------ mouse (Space)
        public static bool MouseFree;
        static void MouseTick()
        {
            if (!Running) { MouseFree = false; return; }
            if (Input.GetKeyDown(KeyCode.Space)) MouseFree = !MouseFree;
            ApplyMouse();
        }

        /// <summary>Also called from LateUpdate and OnGUI: the cutscene keeps re-locking the cursor.</summary>
        public static void ApplyMouse()
        {
            if (!Running || !MouseFree) return;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        public static void Forget() { portal = null; Running = false; current = null; defaultSpot = null; phase = Phase.Idle; }

        /// <summary>Is the crosshair ray pointing at the portal?</summary>
        public static bool Aimed(Ray ray, float maxDist = 6f)
        {
            if (portal == null || !portal.activeInHierarchy) return false;
            Vector3 v = portal.transform.position - ray.origin;
            float along = Vector3.Dot(v, ray.direction);
            if (along < 0.1f || along > maxDist) return false;
            return Vector3.Cross(ray.direction, v).magnitude < 0.45f;
        }

        // ------------------------------------------------------------------ cutscene replay
        public static bool Running { get; private set; }
        static string current;
        public static string Current => current;
        static float notPlayingSince = -1f;
        public static bool Loop = true;
        // PreIntro timeline: 0-3.5s eyes opening from black (+2 dialogs), base animation until ~17s,
        // the phone shows up at ~14.9s. Loop between the two, and start directly at the loop start.
        public static float LoopStart = 3.62f, LoopEnd = 14.85f;
        static float csLength = 30f, pausedSince = -1f;
        static bool loopWarned;
        static readonly string[] preferred = { "PreIntro", "Intro", "Intro2", "TheDream", "SrDream2" };

        public static List<string> CutsceneNames()
        {
            var res = new List<string>();
            try
            {
                var cm = G.CutsceneManager.instance;
                var dict = cm != null ? cm.cutscenesDictionary : null;
                if (dict != null) foreach (var kv in dict) if (!string.IsNullOrEmpty(kv.Key)) res.Add(kv.Key);
            }
            catch (Exception e) { Log.Warning("Cutscenes: " + e.Message); }
            var first = preferred.Where(res.Contains).ToList();
            return first.Concat(res.Except(first).OrderBy(n => n)).ToList();
        }

        enum Phase { Idle, LoadingRoom, Playing, Returning }
        static Phase phase = Phase.Idle;
        static float phaseTime;
        static Vector3 savedPos; static Quaternion savedRot; static string savedLoc;

        static G.LocationManager LM => G.LocationManager.instance;
        static string CurLoc { get { try { return LM != null ? LM.CurrentScene ?? "" : ""; } catch { return ""; } } }
        static bool LocReady(string loc) { try { return LM != null && LM.LocationLoadDone && CurLoc == loc; } catch { return false; } }

        static Transform PlayerRoot()
        {
            var pm = G.PlayerManager.instance;
            if (pm == null) return null;
            return pm.playerController != null ? pm.playerController.transform : pm.transform;
        }

        static void LoadLocation(string loc)
        {
            SandboxMod.Bypass = true;
            try { LM.LoadLocation(loc, false); }
            catch (Exception e) { Log.Warning($"LoadLocation({loc}): " + e.Message); }
            finally { SandboxMod.Bypass = false; }
        }

        public static void Start(string name)
        {
            try
            {
                var cm = G.CutsceneManager.instance;
                if (cm == null || LM == null) { Log.Warning("CutsceneManager/LocationManager not found."); return; }
                DuoScene.Stop();
                Puppets.ReleaseAll();
                var pt = PlayerRoot();
                if (pt != null) { savedPos = pt.position; savedRot = pt.rotation; }
                savedLoc = CurLoc;
                current = name; Running = true;
                GoToRoomThenPlay();
                Log.Msg($"Love Room: looping cutscene '{name}' (started from '{savedLoc}')");
            }
            catch (Exception e) { Log.Error("LoveRoom.Start: " + e); Running = false; }
        }

        /// <summary>The opening cutscenes expect the "loveroom" location to be loaded first.</summary>
        static void GoToRoomThenPlay()
        {
            phaseTime = Time.time;
            if (LocReady("loveroom")) { Play(); return; }
            phase = Phase.LoadingRoom;
            LoadLocation("loveroom");
        }

        static void Play()
        {
            var cm = G.CutsceneManager.instance;
            if (cm == null || current == null) return;
            SandboxMod.Bypass = true;
            try
            {
                if (cm.IsCutscenePlaying()) cm.stopCutscene();
                cm.playCutscene(current);
            }
            catch (Exception e) { Log.Warning("playCutscene: " + e.Message); }
            finally { SandboxMod.Bypass = false; }
            phase = Phase.Playing; phaseTime = Time.time; notPlayingSince = -1f;
        }

        static void TickReplay()
        {
            if (phase == Phase.Idle) return;
            if (phase == Phase.LoadingRoom)
            {
                if (LocReady("loveroom") && Time.time - phaseTime > 0.6f) Play();
                else if (Time.time - phaseTime > 15f) { Log.Warning("Love Room: location 'loveroom' did not load."); Play(); }
                return;
            }
            if (phase == Phase.Returning)
            {
                if (!LocReady(savedLoc) && Time.time - phaseTime < 15f) return;
                // the game places the player at the location spawn after loading: put him back a few frames later
                if (Time.time - phaseTime < 1.0f) return;
                ResetPlayerPose();                                   // first: may move things, the teleport comes after
                CleanCharacters();
                var pt = PlayerRoot();
                var pm = G.PlayerManager.instance;
                if (pt != null)
                {
                    var cc = pm != null ? pm.playerController : null;
                    if (cc != null) cc.enabled = false;
                    pt.SetPositionAndRotation(savedPos, savedRot);
                    if (cc != null) cc.enabled = true;
                }
                phase = Phase.Idle;
                SandboxMod.I.ReturnHome(false);
                Log.Msg("Love Room: back to your starting position.");
                return;
            }
            // Playing
            var cm = G.CutsceneManager.instance;
            if (cm == null) { Running = false; phase = Phase.Idle; return; }
            bool playing = false;
            try { playing = cm.IsCutscenePlaying(); } catch { }
            if (playing)
            {
                notPlayingSince = -1f;
                // Loop only a window at the start of the timeline: the scene never reaches its end,
                // so nothing teleports or chains to the rest of the story.
                if (Loop)
                {
                    try
                    {
                        var cs = cm.currentCutscene;
                        if (cs != null)
                        {
                            csLength = cs.length;
                            float end = Mathf.Max(LoopStart + 0.5f, LoopEnd);
                            if (cs.currentTime >= end || cs.currentTime < LoopStart - 0.01f) cs.currentTime = LoopStart;
                            if (cs.isPaused) { if (pausedSince < 0) pausedSince = Time.time; else if (Time.time - pausedSince > 1.5f) { cs.Resume(); pausedSince = -1f; } }
                            else pausedSince = -1f;
                        }
                    }
                    catch (Exception e) { if (!loopWarned) { loopWarned = true; Log.Warning("Cutscene loop: " + e.Message); } }
                }
                return;
            }
            if (Time.time - phaseTime < 1.5f) return;
            if (notPlayingSince < 0) { notPlayingSince = Time.time; return; }
            if (Time.time - notPlayingSince < 0.8f) return;      // let the end of the cutscene settle
            if (Loop) { Log.Msg("Love Room: scene ended, replaying."); GoToRoomThenPlay(); }
            else Stop();
        }

        /// <summary>The cutscene leaves the player in "doll" mode with its animation: give him back his normal state.</summary>
        static void ResetPlayerPose()
        {
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            try { pm.doll = false; } catch { }
            try { var an = pm.GetAnimator(); if (an != null) { an.speed = 1f; an.Rebind(); } } catch { }
            try { pm.ResetPlayer(); } catch (Exception e) { Log.Warning("ResetPlayer: " + e.Message); }
            try { pm.EnableIK(true); } catch { }
            try { pm.ResetCamAll(); } catch { }
            try { pm.SetControllerEnabled(true); } catch { }
            Log.Msg("Love Room: player reset to normal.");
        }

        /// <summary>The cutscene gives objects to the characters (Sarah's phone...): take them back.</summary>
        static void CleanCharacters()
        {
            foreach (var c in Puppets.Characters())
            {
                try
                {
                    if (!c.gameObject.activeInHierarchy) continue;
                    try { c.destroyActionObjects(); } catch { }
                    foreach (var t in c.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null || t.Pointer == c.transform.Pointer) continue;
                        if (t.name.IndexOf("phone", StringComparison.OrdinalIgnoreCase) >= 0 && t.gameObject.activeSelf)
                        {
                            t.gameObject.SetActive(false);
                            Log.Msg($"Love Room: object '{t.name}' removed from {c.name}");
                        }
                    }
                    try { c.ResetAction(); } catch { }
                }
                catch { }
            }
        }

        public static void Stop()
        {
            if (!Running) return;
            Running = false;
            try { var cm = G.CutsceneManager.instance; if (cm != null && cm.IsCutscenePlaying()) cm.stopCutscene(); } catch { }
            try { var dm = G.DialogManager.INSTANCE; if (dm != null) dm.StopDialogImidiatly(); } catch { }
            Log.Msg("Love Room: stopped.");
            if (string.IsNullOrEmpty(savedLoc)) savedLoc = "home";
            phase = Phase.Returning; phaseTime = Time.time;
            if (!LocReady(savedLoc)) LoadLocation(savedLoc);
        }

        // ------------------------------------------------------------------ portal menu
        public static bool MenuOpen { get; private set; }
        static int page;
        static string filter = "";
        static List<string> names;

        public static void OpenMenu()
        {
            MenuOpen = true; page = 0; filter = ""; names = CutsceneNames();
            SandboxMod.I.BlockPlayer(true);
        }

        public static void CloseMenu()
        {
            MenuOpen = false;
            SandboxMod.I.BlockPlayer(false);
        }

        public static void MenuTick()
        {
            if (!MenuOpen) return;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) CloseMenu();
        }

        public static void DrawMenu()
        {
            if (!MenuOpen) return;
            try
            {
                float w = 460, h = Mathf.Min(Screen.height - 80, 560);
                var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                GUI.Box(r, ""); GUI.Box(r, "");
                GUI.Box(new Rect(r.x, r.y, r.width, 26), "Love Room — which scene to loop?");
                float x = r.x + 10, fw = r.width - 20, y = r.y + 34;
                GUI.Label(new Rect(x, y, 60, 24), "Filter:");
                filter = GUI.TextField(new Rect(x + 60, y, fw - 60, 24), filter ?? "");
                y += 30;
                var list = string.IsNullOrEmpty(filter) ? names : names.Where(n => n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                int per = Mathf.Max(5, (int)((r.yMax - y - 44) / 28f));
                int pages = Mathf.Max(1, (list.Count + per - 1) / per);
                page = Mathf.Clamp(page, 0, pages - 1);
                foreach (var n in list.Skip(page * per).Take(per))
                {
                    string label = preferred.Contains(n) ? "★ " + n : n;
                    if (GUI.Button(new Rect(x, y, fw, 24), label)) { MenuOpen = false; Start(n); return; }
                    y += 28;
                }
                float by = r.yMax - 34;
                if (GUI.Button(new Rect(x, by, 60, 26), "◀")) page = Mathf.Max(0, page - 1);
                GUI.Label(new Rect(x + 70, by + 3, fw - 140, 24), $"page {page + 1}/{pages} — ★ = opening scenes — Esc close");
                if (GUI.Button(new Rect(r.xMax - 70, by, 60, 26), "▶")) page = Mathf.Min(pages - 1, page + 1);
            }
            catch (Exception e) { Log.Warning("LoveRoom menu: " + e.Message); CloseMenu(); }
        }

        // ------------------------------------------------------------------ UI
        public static void DrawBar()
        {
            if (!Running) return;
            ApplyMouse();
            float w = 560, h = 104;
            var r = new Rect((Screen.width - w) / 2f, 12, w, h);
            GUI.Box(r, ""); GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 10, r.y + 12, 200, 22), $"Love Room: {current}");
            Loop = GUI.Toggle(new Rect(r.x + 210, r.y + 12, 80, 22), Loop, " Loop");
            if (GUI.Button(new Rect(r.x + 296, r.y + 8, 100, 28), "Replay")) Play();
            if (GUI.Button(new Rect(r.xMax - 114, r.y + 8, 104, 28), "■ Stop")) Stop();
            float max = Mathf.Max(6f, Mathf.Min(csLength, 60f));
            GUI.Label(new Rect(r.x + 10, r.y + 48, 110, 22), $"Start {LoopStart:0.0}s");
            LoopStart = GUI.HorizontalSlider(new Rect(r.x + 110, r.y + 54, 150, 16), LoopStart, 0f, max - 1f);
            GUI.Label(new Rect(r.x + 280, r.y + 48, 100, 22), $"End {LoopEnd:0.0}s");
            LoopEnd = GUI.HorizontalSlider(new Rect(r.x + 370, r.y + 54, 170, 16), LoopEnd, LoopStart + 0.5f, max);
            var old = GUI.color;
            GUI.color = MouseFree ? Color.green : Color.yellow;
            GUI.Label(new Rect(r.x + 10, r.y + 76, w - 20, 24), MouseFree
                ? "SPACE: hide the mouse   —   mouse active, you can click « Stop »"
                : ">>> Press SPACE to show the mouse (then click « Stop ») <<<");
            GUI.color = old;
        }
    }
}
