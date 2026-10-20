using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Adds a "Sandbox" entry to the main menu, cloned from the game's own "Load" button (same look,
    /// hover animation and sounds). It opens the game's real Load panel, renamed "Sandbox"; the save
    /// picked there is then started in sandbox mode.
    /// </summary>
    internal static class MainMenuButton
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        public static bool SandboxLoad;           // the Load panel was opened from our button
        static GameObject clone;
        static RectTransform cloneRt, loadRt;
        static float nextTry;
        static int failures;
        static readonly List<UnityAction> keepAlive = new List<UnityAction>();

        static Il2CppTMPro.TMP_Text loadTitle;
        static string loadTitleOriginal;

        public static void Reset()
        {
            clone = null; cloneRt = null; loadRt = null; loadTitle = null; loadTitleOriginal = null;
            SandboxLoad = false; failures = 0; keepAlive.Clear();
        }

        public static void Tick()
        {
            if (clone == null && failures < 30 && Time.unscaledTime >= nextTry)
            {
                nextTry = Time.unscaledTime + 0.5f;
                TryCreate();
            }
            UpdateLoadPanelTitle();
        }

        static UnityAction Act(Action a)
        {
            var u = DelegateSupport.ConvertDelegate<UnityAction>(a);
            keepAlive.Add(u);
            return u;
        }

        static void TryCreate()
        {
            try
            {
                var itemsGo = GameObject.Find("Canvas/Panel/RightSide/MenuItems");
                if (itemsGo == null) { failures++; return; }
                var items = itemsGo.transform;
                var load = items.Find("Load");
                if (load == null) { failures++; return; }
                if (items.Find("Sandbox") != null) { clone = items.Find("Sandbox").gameObject; return; }

                clone = UnityEngine.Object.Instantiate(load.gameObject, items);
                clone.name = "Sandbox";
                cloneRt = clone.GetComponent<RectTransform>();
                loadRt = load.GetComponent<RectTransform>();
                var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                clone.transform.SetSiblingIndex(load.GetSiblingIndex() + 1);

                // Remove localisation components that would put "Load" back.
                foreach (var comp in clone.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    string tn = comp.GetIl2CppType().Name;
                    if (tn.Contains("Localize")) UnityEngine.Object.Destroy(comp);
                }
                SetTexts(clone);

                // The clone keeps the persistent "OpenLoadPanel" call; we just remember why it was opened.
                var b = clone.GetComponent<Button>();
                if (b != null) b.onClick.AddListener(Act(() => { SandboxLoad = true; Log.Msg("Menu : Sandbox ouvert"); }));

                // Any other entry cancels the sandbox mode.
                foreach (var other in new[] { "Load", "NewGame", "Galerry", "Settings", "Credits" })
                {
                    var t = items.Find(other);
                    var ob = t != null ? t.GetComponent<Button>() : null;
                    if (ob != null) ob.onClick.AddListener(Act(() => SandboxLoad = false));
                }
                Log.Msg("« Sandbox » button added to the main menu.");
            }
            catch (Exception e)
            {
                failures++;
                Log.Warning("Bouton Sandbox: " + e.Message);
                if (clone != null) { try { UnityEngine.Object.Destroy(clone); } catch { } clone = null; }
            }
        }

        /// <summary>Runs in LateUpdate, after the menu animator: keeps the button beside "Load".</summary>
        public static void LateTick()
        {
            if (clone == null || cloneRt == null || loadRt == null) return;
            try
            {
                cloneRt.anchoredPosition = loadRt.anchoredPosition + new Vector2(SandboxMod.ButtonOffsetX.Value, SandboxMod.ButtonOffsetY.Value);
                cloneRt.localScale = loadRt.localScale;
            }
            catch { }
        }

        static void SetTexts(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true))
                if (t != null && t.text != "Sandbox") t.text = "Sandbox";
        }

        static void UpdateLoadPanelTitle()
        {
            try
            {
                if (clone != null) SetTexts(clone);   // cheap, keeps the label if something resets it

                if (loadTitle == null)
                {
                    var go = GameObject.Find("Canvas/Panel/LoadPanel/RightSide/Text (TMP)");
                    if (go == null) return;
                    loadTitle = go.GetComponent<Il2CppTMPro.TMP_Text>();
                    if (loadTitle == null) return;
                    loadTitleOriginal = loadTitle.text;
                }
                string want = SandboxLoad ? "Sandbox" : loadTitleOriginal;
                if (want != null && loadTitle.text != want) loadTitle.text = want;
            }
            catch { loadTitle = null; }
        }
    }
}
