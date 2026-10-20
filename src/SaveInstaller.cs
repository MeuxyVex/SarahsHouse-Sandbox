using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace SandboxHouse
{
    /// <summary>
    /// Ships a ready-made save (day 63, everybody in the house) with the mod: on first launch it is copied into
    /// the game's save folder on a free slot. Existing saves are never overwritten.
    /// Source: UserData/SandboxHouse/sandbox_save.dat (+ sandbox_save.jpg thumbnail).
    /// </summary>
    internal static class SaveInstaller
    {
        public static void Run(MelonLogger.Instance log)
        {
            try
            {
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "SandboxHouse");
                string src = Path.Combine(dir, "sandbox_save.dat");
                string flag = Path.Combine(dir, "sandbox_save.installed");
                if (!File.Exists(src) || File.Exists(flag)) return;

                string saves = Application.persistentDataPath;
                Directory.CreateDirectory(saves);
                for (int slot = 1; slot <= 9; slot++)
                {
                    string dst = Path.Combine(saves, $"save{slot}.dat");
                    if (File.Exists(dst)) continue;
                    File.Copy(src, dst);
                    string jpg = Path.Combine(dir, "sandbox_save.jpg");
                    if (File.Exists(jpg)) File.Copy(jpg, Path.Combine(saves, $"save{slot}.jpg"), true);
                    File.WriteAllText(flag, $"installed as save{slot}.dat on {DateTime.Now:yyyy-MM-dd HH:mm}");
                    log.Msg($"Sandbox save installed in slot {slot} ({dst}).");
                    return;
                }
                log.Warning("Sandbox save: no free slot (1-9), nothing installed.");
            }
            catch (Exception e) { log.Warning("Sandbox save install: " + e.Message); }
        }
    }
}
