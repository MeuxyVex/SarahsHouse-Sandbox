using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Unlocks every gallery entry once the gallery panel is shown: each scene gets an unlocked
    /// button that calls the game's own GalleryMenu.StartLoveScene (the normal replay path).
    /// </summary>
    internal static class Gallery
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;
        static float nextCheck;
        static IntPtr doneFor = IntPtr.Zero;   // GalleryMenu already processed (until it is disabled)
        static readonly List<UnityAction> keepAlive = new List<UnityAction>();

        public static void Reset() { doneFor = IntPtr.Zero; keepAlive.Clear(); }

        public static void Tick()
        {
            if (!SandboxMod.UnlockGallery || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.4f;

            G.GalleryMenu gm = null;
            try { gm = UnityEngine.Object.FindObjectOfType<G.GalleryMenu>(); } catch { }
            if (gm == null || !gm.gameObject.activeInHierarchy) { doneFor = IntPtr.Zero; return; }
            if (gm.Pointer == doneFor) return;

            // Wait until the game has built its buttons.
            var buttons = gm.buttonList;
            if (buttons == null) return;
            doneFor = gm.Pointer;
            UnlockAll(gm);
        }

        static void UnlockAll(G.GalleryMenu gm)
        {
            int fixedCount = 0, created = 0, total = 0;
            try
            {
                var scenes = gm.loveScenes;
                var buttons = gm.buttonList;
                var byScene = new Dictionary<IntPtr, G.GalleryButton>();
                foreach (var b in buttons)
                {
                    if (b == null) continue;
                    G.LoveScene s = null;
                    try { s = b.GetLoveScene(); } catch { }
                    if (s != null && !byScene.ContainsKey(s.Pointer)) byScene[s.Pointer] = b;
                }

                if (scenes == null) { Log.Warning("Gallery: empty scene list."); return; }
                foreach (var scene in scenes)
                {
                    if (scene == null) continue;
                    total++;
                    try
                    {
                        if (!byScene.TryGetValue(scene.Pointer, out var btn) || btn == null)
                        {
                            // Scene not listed at all: create its button from the game's prefab.
                            btn = UnityEngine.Object.Instantiate(gm.galleryButtonPrefab, gm.contentContainer);
                            btn.AssignScene(scene);
                            buttons.Add(btn);
                            created++;
                        }
                        if (!btn.gameObject.activeSelf) btn.gameObject.SetActive(true);
                        btn.SetUnlocked();
                        var ub = btn.Button;
                        if (ub != null)
                        {
                            ub.interactable = true;
                            ub.onClick.RemoveAllListeners();
                            var sc = scene; var menu = gm;
                            var act = DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Play(menu, sc)));
                            keepAlive.Add(act);
                            ub.onClick.AddListener(act);
                        }
                        fixedCount++;
                    }
                    catch (Exception e) { Log.Warning("Gallery, scene skipped: " + e.Message); }
                }
            }
            catch (Exception e) { Log.Warning("Gallery: " + e); }
            Log.Msg($"Gallery: {fixedCount}/{total} scenes unlocked ({created} button(s) added).");
        }

        static void Play(G.GalleryMenu gm, G.LoveScene scene)
        {
            try
            {
                Log.Msg("Gallery: starting scene " + SafeName(scene));
                gm.StartLoveScene(scene);
            }
            catch (Exception e) { Log.Error("Gallery StartLoveScene: " + e); }
        }

        static string SafeName(G.LoveScene s)
        {
            try { return s.UID; } catch { return "?"; }
        }
    }
}
