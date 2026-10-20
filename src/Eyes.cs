using System;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// No blinking / closed eyes for the player in the sandbox: some animations close the player's eyes,
    /// which the game shows as a full-screen black image (CameraBlink). We keep the eyes open and hide that image.
    /// </summary>
    internal static class Eyes
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;
        static UnityEngine.UI.Image hiddenImage;
        static bool logged;
        static float nextOpen;

        public static bool ShouldBlock => SandboxMod.Active || DuoScene.Active || FreeCam.Active;

        public static void LateTick()
        {
            if (!ShouldBlock) { Restore(); return; }
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            try
            {
                pm.startBlink = false;
                pm.blinkInProgress = false;
                if (Time.unscaledTime >= nextOpen) { nextOpen = Time.unscaledTime + 0.15f; pm.CloseEyesScreen(false); }
                var bc = pm.blinkController;
                if (bc != null)
                {
                    var img = bc.blinkImage;
                    if (img != null && img.enabled)
                    {
                        img.enabled = false;
                        hiddenImage = img;
                        if (!logged) { logged = true; Log.Msg("Player eye blinking disabled (sandbox)."); }
                    }
                }
            }
            catch (Exception e) { if (!logged) { logged = true; Log.Warning("Eyes: " + e.Message); } }
        }

        public static void Restore()
        {
            if (hiddenImage == null) return;
            try { hiddenImage.enabled = true; } catch { }
            hiddenImage = null;
            logged = false;
        }

        public static void Forget() { hiddenImage = null; logged = false; }
    }
}
