using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Sandbox replacement for the game's dialog: talking to a character (or using a locked door)
    /// opens this small menu instead.
    /// </summary>
    internal static class Interaction
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        public static bool IsOpen => ch != null || door != null;
        static G.Character ch;
        static G.Door door;
        static int page;            // 0 = main options, 1 = animations
        static int animPage;
        static int scenePage;
        static string filter = "";
        static bool allClips;
        static List<AnimationClip> clips;
        static float closedAt = -10f;
        static string info = "";

        // Pending requests coming from Harmony prefixes (opened on next Update, never inside the hook).
        static G.Character pendingCh;
        static G.Door pendingDoor;

        public static void RequestCharacter(G.Character c)
        {
            if (DuoScene.Active) return;
            if (IsOpen || Time.unscaledTime - closedAt < 0.6f) return;
            pendingCh = c;
        }

        public static void RequestDoor(G.Door d)
        {
            if (IsOpen || Time.unscaledTime - closedAt < 0.6f) return;
            pendingDoor = d;
        }

        public static void Tick()
        {
            if (pendingCh != null) { var c = pendingCh; pendingCh = null; OpenCharacter(c); }
            if (pendingDoor != null) { var d = pendingDoor; pendingDoor = null; OpenDoor(d); }
            if (!IsOpen) return;

            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) { if (page != 0) page = 0; else Close(); return; }
            if (page == 0)
            {
                var opts = Options();
                for (int i = 0; i < opts.Count && i < 9; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) { opts[i].Value(); break; }
            }
        }

        public static void OpenCharacter(G.Character c)
        {
            if (c == null) return;
            ch = c; door = null; page = 0; animPage = 0; clips = null; filter = ""; info = "";
            SandboxMod.I.BlockPlayer(true);
            Log.Msg($"Interaction: {c.name}");
        }

        public static void OpenDoor(G.Door d)
        {
            if (d == null) return;
            door = d; ch = null; page = 0; info = "";
            SandboxMod.I.BlockPlayer(true);
            Log.Msg($"Door interaction: {d.name} (locked={SafeLocked(d)})");
        }

        public static void Close()
        {
            ch = null; door = null; closedAt = Time.unscaledTime;
            SandboxMod.I.BlockPlayer(false);
        }

        // ------------------------------------------------------------------ actions
        static List<KeyValuePair<string, Action>> Options()
        {
            var o = new List<KeyValuePair<string, Action>>();
            if (ch != null)
            {
                var c = ch;
                var p = Puppets.Get(c);
                if (Clothes.IsNaked(c)) o.Add(new KeyValuePair<string, Action>("Put your clothes back on", () => { Clothes.SetNaked(c, false); info = "Clothes on."; }));
                else o.Add(new KeyValuePair<string, Action>("Take your clothes off", () => { Clothes.SetNaked(c, true); info = "Clothes off."; }));
                o.Add(new KeyValuePair<string, Action>("Play an animation…", () => { page = 1; animPage = 0; clips = null; }));
                o.Add(new KeyValuePair<string, Action>("Two-person scene…", () => { page = 2; scenePage = 0; }));
                if (p != null && p.Clip != null)
                    o.Add(new KeyValuePair<string, Action>("Stop the animation (idle)", () => Puppets.PlayIdle(p)));
                o.Add(new KeyValuePair<string, Action>("Come in front of me", () => Puppets.BringInFront(new List<G.Character> { c }, 1.4f)));
                if (p == null) o.Add(new KeyValuePair<string, Action>("Stay right there", () => Puppets.Take(c, c.transform.position, c.transform.rotation)));
                else o.Add(new KeyValuePair<string, Action>("You can go (release)", () => { Puppets.Release(p); Close(); }));
                o.Add(new KeyValuePair<string, Action>("Goodbye", Close));
            }
            else if (door != null)
            {
                var d = door;
                bool locked = SafeLocked(d);
                if (locked) o.Add(new KeyValuePair<string, Action>("Unlock the door", () => { Doors.SetLocked(d, false); info = "Door unlocked."; }));
                else o.Add(new KeyValuePair<string, Action>("Lock the door", () => { Doors.SetLocked(d, true); info = "Door locked."; }));
                o.Add(new KeyValuePair<string, Action>("Open", () => { Doors.SetLocked(d, false); Doors.Open(d, true); Close(); }));
                o.Add(new KeyValuePair<string, Action>("Close", () => { Doors.Open(d, false); Close(); }));
                o.Add(new KeyValuePair<string, Action>("Cancel", Close));
            }
            return o;
        }

        static bool SafeLocked(G.Door d) { try { return d.isLocked(); } catch { return false; } }

        static void ClothesOff(G.Character c)
        {
            try { c.uneqipAllClothing(); info = "Clothes off."; Log.Msg($"{c.name}: clothes off"); }
            catch (Exception e) { info = "Error, see the log."; Log.Warning("ClothesOff: " + e); }
        }

        static void ClothesOn(G.Character c)
        {
            try { c.EquipStandartClothing(); info = "Clothes on."; Log.Msg($"{c.name}: clothes on"); }
            catch (Exception e) { info = "Error, see the log."; Log.Warning("ClothesOn: " + e); }
        }

        static void PlayOnCharacter(G.Character c, AnimationClip clip)
        {
            var p = Puppets.Get(c);
            if (p == null)
            {
                // Freeze the character right in front of the player so the animation happens "devant toi".
                Puppets.BringInFront(new List<G.Character> { c }, 1.6f);
                p = Puppets.Get(c);
            }
            if (p != null) Puppets.Play(p, clip, SandboxMod.LoopAnims);
        }

        // ------------------------------------------------------------------ drawing
        public static void Draw()
        {
            if (!IsOpen) return;
            try
            {
                if (ch == null && door == null) return;
                if (page == 1 && ch != null) { DrawAnims(); return; }
                if (page == 2 && ch != null) { DrawScenes(); return; }

                var opts = Options();
                float w = 380, h = 64 + opts.Count * 32 + (string.IsNullOrEmpty(info) ? 0 : 28);
                var r = new Rect((Screen.width - w) / 2f, Screen.height * 0.55f, w, h);
                GUI.Box(r, ""); GUI.Box(r, "");
                string title = ch != null ? ch.name : $"Door ({(SafeLocked(door) ? "locked" : "unlocked")})";
                GUI.Box(new Rect(r.x, r.y, r.width, 26), title);
                float y = r.y + 34;
                for (int i = 0; i < opts.Count; i++)
                {
                    if (GUI.Button(new Rect(r.x + 12, y, r.width - 24, 28), $"{i + 1}.  {opts[i].Key}")) { opts[i].Value(); return; }
                    y += 32;
                }
                if (!string.IsNullOrEmpty(info)) GUI.Label(new Rect(r.x + 12, y, r.width - 24, 24), info);
            }
            catch (Exception e) { Log.Warning("Interaction.Draw: " + e.Message); Close(); }
        }

        static void DrawScenes()
        {
            float w = 520, h = Mathf.Min(Screen.height - 80, 560);
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.Box(r, ""); GUI.Box(r, "");
            GUI.Box(new Rect(r.x, r.y, r.width, 26), $"{ch.name} — two-person scenes (in the house)");
            float x = r.x + 10, fw = r.width - 20, y = r.y + 32;
            if (GUI.Button(new Rect(x, y, 90, 26), "◀ Back")) { page = 0; return; }
            y += 32;

            var list = LoveScenes.For(ch);
            if (list.Count == 0) { GUI.Label(new Rect(x, y, fw, 44), "No scene found for this character\n(or scenes not loaded yet)."); return; }
            int perPage = Mathf.Max(4, (int)((r.yMax - y - 44) / 30f));
            int pages = Mathf.Max(1, (list.Count + perPage - 1) / perPage);
            scenePage = Mathf.Clamp(scenePage, 0, pages - 1);
            foreach (var sc in list.Skip(scenePage * perPage).Take(perPage))
            {
                if (GUI.Button(new Rect(x, y, fw, 26), LoveScenes.Title(sc))) { var s2 = sc; Close(); DuoScene.Start(s2); return; }
                y += 30;
            }
            float by = r.yMax - 34;
            if (GUI.Button(new Rect(x, by, 60, 26), "◀")) scenePage = Mathf.Max(0, scenePage - 1);
            GUI.Label(new Rect(x + 70, by + 3, fw - 140, 24), $"page {scenePage + 1}/{pages} — {list.Count} scenes");
            if (GUI.Button(new Rect(r.xMax - 70, by, 60, 26), "▶")) scenePage = Mathf.Min(pages - 1, scenePage + 1);
        }

        static void DrawAnims()
        {
            float w = 460, h = Mathf.Min(Screen.height - 80, 640);
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.Box(r, ""); GUI.Box(r, "");
            GUI.Box(new Rect(r.x, r.y, r.width, 26), $"{ch.name} — pick an animation");
            float y = r.y + 32;
            float x = r.x + 10, fw = r.width - 20;

            if (GUI.Button(new Rect(x, y, 90, 26), "◀ Back")) { page = 0; return; }
            SandboxMod.LoopAnims = GUI.Toggle(new Rect(x + 100, y + 3, 110, 22), SandboxMod.LoopAnims, " Loop");
            bool all = GUI.Toggle(new Rect(x + 215, y + 3, fw - 215, 22), allClips, " All game animations");
            if (all != allClips) { allClips = all; clips = null; animPage = 0; }
            y += 32;

            GUI.Label(new Rect(x, y, 60, 24), "Filter:");
            string nf = GUI.TextField(new Rect(x + 60, y, fw - 60, 24), filter ?? "");
            if (nf != filter) { filter = nf; animPage = 0; }
            y += 30;

            var pp = Puppets.Get(ch);
            GUI.Label(new Rect(x, y + 3, 70, 22), "Height:");
            if (GUI.Button(new Rect(x + 70, y, 40, 24), "▲") && pp != null) pp.ManualY += 0.05f;
            if (GUI.Button(new Rect(x + 114, y, 40, 24), "▼") && pp != null) pp.ManualY -= 0.05f;
            if (GUI.Button(new Rect(x + 158, y, 50, 24), "0") && pp != null) pp.ManualY = 0f;
            GUI.Label(new Rect(x + 214, y + 3, 70, 22), pp != null ? $"{pp.ManualY * 100f:+0;-0;0} cm" : "");
            Puppets.AutoGround = GUI.Toggle(new Rect(x + 290, y + 3, fw - 290, 22), Puppets.AutoGround, " Snap to floor");
            y += 30;

            if (clips == null) clips = allClips ? Puppets.AllLoadedClips() : Puppets.ClipsOf(ch);
            var list = string.IsNullOrEmpty(filter) ? clips
                : clips.Where(c => c != null && c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            int perPage = Mathf.Max(5, (int)((r.yMax - y - 44) / 26f));
            int pages = Mathf.Max(1, (list.Count + perPage - 1) / perPage);
            animPage = Mathf.Clamp(animPage, 0, pages - 1);

            var p = Puppets.Get(ch);
            foreach (var clip in list.Skip(animPage * perPage).Take(perPage))
            {
                if (clip == null) continue;
                bool cur = p != null && p.Clip != null && p.Clip.Pointer == clip.Pointer;
                if (GUI.Button(new Rect(x, y, fw, 24), $"{(cur ? "▶ " : "")}{clip.name}  ({clip.length:0.0}s)")) PlayOnCharacter(ch, clip);
                y += 26;
            }

            float by = r.yMax - 34;
            if (GUI.Button(new Rect(x, by, 60, 26), "◀")) animPage = Mathf.Max(0, animPage - 1);
            GUI.Label(new Rect(x + 70, by + 3, fw - 140, 24), $"page {animPage + 1}/{pages} — {list.Count} anims — Esc = back");
            if (GUI.Button(new Rect(r.xMax - 70, by, 60, 26), "▶")) animPage = Mathf.Min(pages - 1, animPage + 1);
        }
    }

    // ======================================================================
    internal static class Doors
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;
        public static bool KeepAllUnlocked;
        static float nextApply;

        public static List<G.Door> All()
        {
            var res = new List<G.Door>();
            try
            {
                var gm = G.GameManager.INSTANCE;
                var l = gm != null ? gm.GetAllDoors() : null;
                if (l != null) foreach (var d in l) if (d != null) res.Add(d);
            }
            catch (Exception e) { Log.Warning("Doors.All: " + e.Message); }
            if (res.Count == 0)
            {
                try { foreach (var d in UnityEngine.Object.FindObjectsOfType<G.Door>()) if (d != null) res.Add(d); } catch { }
            }
            return res;
        }

        public static void SetLocked(G.Door d, bool locked)
        {
            try { d.setLocked(locked); Log.Msg($"Door {d.name}: locked={locked}"); }
            catch (Exception e) { Log.Warning("SetLocked: " + e.Message); }
        }

        public static void Open(G.Door d, bool open)
        {
            try { d.changeState(open ? G.Door.DoorState.Opened : G.Door.DoorState.Closed); }
            catch (Exception e) { Log.Warning("Door.changeState: " + e.Message); }
        }

        public static int UnlockAll()
        {
            int n = 0;
            foreach (var d in All())
            {
                try { if (d.isLocked()) { d.setLocked(false); n++; } } catch { }
            }
            if (n > 0) Log.Msg($"{n} door(s) unlocked");
            return n;
        }

        public static void Tick()
        {
            if (!KeepAllUnlocked || Time.unscaledTime < nextApply) return;
            nextApply = Time.unscaledTime + 2f;
            UnlockAll();
        }

        public static G.Door Pick(Ray ray, float maxDist = 4f)
        {
            G.Door best = null; float bestOff = 0.9f;
            foreach (var d in All())
            {
                try
                {
                    if (!d.gameObject.activeInHierarchy) continue;
                    Vector3 c = d.transform.position;
                    var col = d.GetComponentInChildren<Collider>();
                    if (col != null) c = col.bounds.center;
                    Vector3 v = c - ray.origin;
                    float along = Vector3.Dot(v, ray.direction);
                    if (along < 0.1f || along > maxDist) continue;
                    float off = Vector3.Cross(ray.direction, v).magnitude;
                    if (off < bestOff) { bestOff = off; best = d; }
                }
                catch { }
            }
            return best;
        }
    }
}
