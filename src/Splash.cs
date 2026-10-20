using System;
using System.Globalization;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace SandboxHouse
{
    /// <summary>
    /// "Modded by Vex" splash, shown right after the engine splash (ACE STUDIO / Made with Unity),
    /// i.e. when the Disclamer scene appears. Same style: logos fade in/out on the background colour.
    /// Images: UserData/SandboxHouse/vex_round.png and modded_by_vex.png
    /// </summary>
    internal static class Splash
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        static bool shown;
        static GameObject root;
        static CanvasGroup all, logos;
        static RectTransform logosRt;
        static float t0;
        static bool waiting;
        static float createdAt;

        const float FadeIn = 0.6f, Hold = 2.0f, FadeOut = 0.6f, Reveal = 0.45f;

        public static void OnDisclaimerLoaded()
        {
            if (shown || !SandboxMod.ShowSplash.Value) return;
            shown = true;
            try { Create(); }
            catch (Exception e) { Log.Warning("Splash: " + e); Destroy(); }
        }

        static Texture2D LoadTex(string file)
        {
            string path = Path.Combine(MelonEnvironment.UserDataDirectory, "SandboxHouse", file);
            if (!File.Exists(path)) { Log.Warning("Splash: missing image " + path); return null; }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) { Log.Warning("Splash: LoadImage failed " + file); return null; }
            tex.wrapMode = TextureWrapMode.Clamp;
            UnityEngine.Object.DontDestroyOnLoad(tex);
            return tex;
        }

        static RectTransform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            return rt;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        static void Create()
        {
            var avatar = LoadTex("vex_logo.png") ?? LoadTex("vex_round.png");
            var text = LoadTex("modded_by_vex.png");
            if (avatar == null && text == null) return;

            root = new GameObject("VexSplash");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();     // blocks clicks on the disclaimer behind
            all = root.AddComponent<CanvasGroup>();

            var bg = Child(root.transform, "Background");
            Stretch(bg);
            var bgImg = bg.gameObject.AddComponent<Image>();
            bgImg.color = ParseColor(SandboxMod.SplashColor.Value, new Color(0.137f, 0.122f, 0.125f));

            logosRt = Child(root.transform, "Logos");
            Stretch(logosRt);
            logos = logosRt.gameObject.AddComponent<CanvasGroup>();
            logos.alpha = 0f;

            float y = text != null ? 120f : 0f;
            if (avatar != null)
            {
                var a = Child(logosRt, "Avatar");
                float ah = 460f, aw = ah * avatar.width / Mathf.Max(1, avatar.height);
                a.sizeDelta = new Vector2(aw, ah);
                a.anchoredPosition = new Vector2(0, y + 30f);
                a.gameObject.AddComponent<RawImage>().texture = avatar;
            }
            if (text != null)
            {
                var t = Child(logosRt, "Text");
                float w = 560f, h = w * text.height / Mathf.Max(1, text.width);
                t.sizeDelta = new Vector2(w, h);
                t.anchoredPosition = new Vector2(0, avatar != null ? -250f : 0f);
                t.gameObject.AddComponent<RawImage>().texture = text;
            }
            createdAt = Time.unscaledTime;
            waiting = true;   // the engine splash (ACE STUDIO / Unity) is drawn above everything: wait for its end
            Log.Msg("« Modded by Vex » screen ready, waiting for the Unity splash to end.");
        }

        public static void Tick()
        {
            if (root == null) return;
            try
            {
                if (waiting)
                {
                    bool done;
                    try { done = UnityEngine.Rendering.SplashScreen.isFinished; } catch { done = true; }
                    if (!done && Time.unscaledTime - createdAt < 10f) return;
                    waiting = false;
                    t0 = Time.unscaledTime;
                    Log.Msg($"Unity splash ended ({Time.unscaledTime - createdAt:0.0}s): showing Modded by Vex.");
                }
                float t = Time.unscaledTime - t0;
                float end = FadeIn + Hold + FadeOut;

                // Skip with any key / click, like the game's own intro.
                if (t > 0.3f && t < end && (Input.anyKeyDown || Input.GetMouseButtonDown(0))) { t0 = Time.unscaledTime - end; t = end; }

                float a = t < FadeIn ? t / FadeIn : t < FadeIn + Hold ? 1f : t < end ? 1f - (t - FadeIn - Hold) / FadeOut : 0f;
                logos.alpha = Mathf.Clamp01(a);
                float s = 1f + 0.05f * Mathf.Clamp01(t / end);       // slow zoom, like Unity's splash
                logosRt.localScale = new Vector3(s, s, 1f);

                if (t > end) all.alpha = Mathf.Clamp01(1f - (t - end) / Reveal);
                if (t > end + Reveal) Destroy();
            }
            catch (Exception e) { Log.Warning("Splash.Tick: " + e.Message); Destroy(); }
        }

        static void Destroy()
        {
            if (root != null) { try { UnityEngine.Object.Destroy(root); } catch { } }
            root = null;
        }

        static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                hex = (hex ?? "").Trim().TrimStart('#');
                if (hex.Length != 6) return fallback;
                int v = int.Parse(hex, NumberStyles.HexNumber);
                return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
            }
            catch { return fallback; }
        }
    }
}
