using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Starts the game's own two-person scenes (player + character animations, sounds, cameras, UI)
    /// through LoveSceneManager, from the sandbox interaction menu.
    /// </summary>
    internal static class LoveScenes
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;
        static List<G.LoveScene> cache;

        public static bool Running
        {
            get
            {
                try
                {
                    var gm = G.GameManager.INSTANCE;
                    if (gm != null && gm.isInHScene) return true;
                    var lm = G.LoveSceneManager.INSTANCE;
                    return lm != null && lm.IsLoveSceneRunning();
                }
                catch { return false; }
            }
        }

        public static List<G.LoveScene> All()
        {
            if (cache != null && cache.Count > 0) return cache;
            var res = new List<G.LoveScene>();
            try
            {
                // All two-person scenes live in Resources/scriptableobjects/hscenes.
                foreach (var o in Resources.LoadAll("scriptableobjects/hscenes", Il2CppType.Of<G.LoveScene>()))
                {
                    var s = o != null ? o.TryCast<G.LoveScene>() : null;
                    if (s != null && !res.Any(x => x.Pointer == s.Pointer)) res.Add(s);
                }
            }
            catch (Exception e) { Log.Warning("LoveScenes.LoadAll(hscenes): " + e.Message); }
            try
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<G.LoveScene>())
                    if (s != null && !res.Any(x => x.Pointer == s.Pointer)) res.Add(s);
            }
            catch (Exception e) { Log.Warning("LoveScenes.All: " + e.Message); }
            if (res.Count == 0)
            {
                try
                {
                    foreach (var o in Resources.LoadAll("", Il2CppType.Of<G.LoveScene>()))
                    {
                        var s = o != null ? o.TryCast<G.LoveScene>() : null;
                        if (s != null) res.Add(s);
                    }
                }
                catch (Exception e) { Log.Warning("LoveScenes.LoadAll: " + e.Message); }
            }
            Log.Msg($"Two-person scenes found: {res.Count}");
            cache = res.OrderBy(Title).ToList();
            return cache;
        }

        public static void Reset() { cache = null; owners = null; }

        public static bool IsTest(G.LoveScene s)
        {
            try { return s.name.StartsWith("Test", StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        static Dictionary<IntPtr, G.LoveScene> owners;
        /// <summary>The (non-test) scene that starts with this pose: its placements are the right ones for it.</summary>
        public static G.LoveScene OwnerOf(G.LovePose pose)
        {
            if (pose == null) return null;
            if (owners == null)
            {
                owners = new Dictionary<IntPtr, G.LoveScene>();
                foreach (var s in All())
                {
                    if (IsTest(s)) continue;
                    try
                    {
                        var sp = s.startingPose;
                        if (sp != null && !owners.ContainsKey(sp.Pointer)) owners[sp.Pointer] = s;
                        if (s.dynamicStartingPoses != null)
                            foreach (var d in s.dynamicStartingPoses)
                                if (d != null && d.pose != null && !owners.ContainsKey(d.pose.Pointer)) owners[d.pose.Pointer] = s;
                    }
                    catch { }
                }
            }
            return owners.TryGetValue(pose.Pointer, out var o) ? o : null;
        }

        public static string Title(G.LoveScene s)
        {
            try
            {
                string n = s.sceneName;
                if (string.IsNullOrEmpty(n)) n = s.name;
                string loc = "";
                try { loc = s.Location; } catch { }
                return string.IsNullOrEmpty(loc) ? n : $"{n}  [{loc}]";
            }
            catch { return "?"; }
        }

        /// <summary>Scenes in which the given character takes part.</summary>
        public static List<G.LoveScene> For(G.Character c)
        {
            string name = c != null ? c.name : "";
            var res = new List<G.LoveScene>();
            foreach (var s in All())
            {
                try
                {
                    string loc = "";
                    try { loc = s.Location ?? ""; } catch { }
                    if (!string.Equals(loc, "home", StringComparison.OrdinalIgnoreCase)) continue;   // house only
                    bool found = false;
                    var inc = s.includingCharacters;
                    if (inc != null)
                        foreach (var ch in inc)
                            if (string.Equals(ch.ToString(), name, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    if (!found && s.characters != null)
                        foreach (var lc in s.characters)
                            if (lc != null && string.Equals(lc.character.ToString(), name, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    if (found) res.Add(s);
                }
                catch { }
            }
            return res;
        }

        public static void Start(G.LoveScene scene)
        {
            try
            {
                var lm = G.LoveSceneManager.INSTANCE;
                if (lm == null) { Log.Warning("LoveSceneManager not found."); return; }
                if (lm.IsLoveSceneRunning()) { Log.Msg("A scene is already running."); return; }

                // Our frozen characters would fight the scene's positioning: give them back to the game.
                Puppets.ReleaseAll();

                Log.Msg("Starting scene: " + Title(scene));
                SandboxMod.Bypass = true;
                try { lm.StartLoveSceneBlackOut(scene); }
                catch (Exception e)
                {
                    Log.Warning("StartLoveSceneBlackOut failed, trying direct: " + e.Message);
                    lm.StartLoveScene(scene);
                }
                finally { SandboxMod.Bypass = false; }
            }
            catch (Exception e) { Log.Error("LoveScenes.Start: " + e); }
        }
    }
}
