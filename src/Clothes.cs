using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Single source of truth for "who is undressed" in the sandbox. The game re-dresses characters on its
    /// own (action change, OnEnable after a freeze, end of a two-person scene...), so the state is re-applied
    /// regularly: what you set in F9 / the interaction menu is what you get in the duo scenes too.
    /// </summary>
    internal static class Clothes
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;
        public const string PlayerKey = "__player__";

        static readonly HashSet<string> naked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static float nextApply;

        public static bool IsNaked(G.Character c) => c != null && naked.Contains(c.name);
        public static bool PlayerNaked => naked.Contains(PlayerKey);

        public static void SetNaked(G.Character c, bool value)
        {
            if (c == null) return;
            if (value) naked.Add(c.name); else naked.Remove(c.name);
            try
            {
                if (value) c.uneqipAllClothing();
                else c.EquipStandartClothing();
                Log.Msg($"{c.name}: {(value ? "undressed" : "dressed")}");
            }
            catch (Exception e) { Log.Warning("Clothes " + c.name + ": " + e.Message); }
        }

        public static void Toggle(G.Character c) => SetNaked(c, !IsNaked(c));

        public static void SetPlayerNaked(bool value)
        {
            if (value) naked.Add(PlayerKey); else naked.Remove(PlayerKey);
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            try
            {
                if (value) pm.PutAllClothingOff(); else pm.PutAllClothingOn();
                Log.Msg($"Player: {(value ? "undressed" : "dressed")}");
            }
            catch (Exception e) { Log.Warning("Clothes player: " + e.Message); }
        }

        public static void TogglePlayer() => SetPlayerNaked(!PlayerNaked);

        /// <summary>Immediately re-applies the state (call after anything that may re-dress).</summary>
        public static void Reapply(G.Character c)
        {
            if (c == null || !IsNaked(c)) return;
            try { c.uneqipAllClothing(); } catch { }
        }

        public static void Tick()
        {
            if (naked.Count == 0 || Time.unscaledTime < nextApply) return;
            nextApply = Time.unscaledTime + 0.5f;
            foreach (var c in Puppets.Characters())
            {
                if (!naked.Contains(c.name)) continue;
                try
                {
                    if (!c.gameObject.activeInHierarchy) continue;
                    var on = c.activeClothing;               // only re-strip if the game dressed them again
                    if (on == null || on.Count > 0) c.uneqipAllClothing();
                }
                catch { }
            }
            if (PlayerNaked)
            {
                var pm = G.PlayerManager.instance;
                try
                {
                    if (pm != null && pm.currentClothingOn != null && pm.currentClothingOn.Count > 0) pm.PutAllClothingOff();
                }
                catch { }
            }
        }

        public static void Forget() { naked.Clear(); }
    }
}
