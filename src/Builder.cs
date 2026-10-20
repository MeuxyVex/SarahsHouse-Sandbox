using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using UnityEngine.Playables;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// The player as a "puppet": frozen at a chosen place, playing a solo animation. Used by the pose builder.
    /// </summary>
    internal static class PlayerPose
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        public static bool Active { get; private set; }
        public static Vector3 Pos; public static Quaternion Rot;
        public static AnimationClip Clip;
        public static bool Loop = true;
        static float clipStart;
        static bool hasGraph, sampling;
        static PlayableGraph graph;
        static Vector3 savedPos; static Quaternion savedRot;

        static Transform Root()
        {
            var pm = G.PlayerManager.instance;
            if (pm == null) return null;
            return pm.playerController != null ? pm.playerController.transform : pm.transform;
        }

        public static Animator Anim() { try { return G.PlayerManager.instance?.GetAnimator(); } catch { return null; } }

        public static void Begin()
        {
            if (Active) return;
            var pm = G.PlayerManager.instance; var r = Root();
            if (pm == null || r == null) return;
            savedPos = r.position; savedRot = r.rotation;
            Pos = r.position; Rot = r.rotation;
            try { pm.setInputAllowed(false); pm.allowMovement(false); } catch { }
            if (pm.playerController != null) pm.playerController.enabled = false;
            try { pm.SetBipedIKEnabled(false); } catch { }
            Active = true;
            Log.Msg("Player frozen for the pose builder.");
        }

        public static void Play(AnimationClip clip, bool loop)
        {
            Begin();
            StopClip();
            Clip = clip; Loop = loop; clipStart = Time.time;
            var an = Anim();
            if (an == null || clip == null) return;
            if (Puppets.TryPlayGraph(an, clip, out graph)) { hasGraph = true; sampling = false; }
            else { an.enabled = false; sampling = true; }
            Log.Msg($"Player plays '{clip.name}'");
        }

        public static void StopClip()
        {
            if (hasGraph) { try { graph.Destroy(); } catch { } hasGraph = false; }
            if (sampling) { sampling = false; try { var an = Anim(); if (an != null) an.enabled = true; } catch { } }
            Clip = null;
        }

        /// <summary>Gives the player back his normal state (stays where he was placed unless backToStart).</summary>
        public static void End(bool backToStart)
        {
            if (!Active) return;
            StopClip();
            Active = false;
            var pm = G.PlayerManager.instance; var r = Root();
            try { var an = Anim(); if (an != null) { an.enabled = true; an.Rebind(); } } catch { }
            if (r != null && backToStart) r.SetPositionAndRotation(savedPos, savedRot);
            if (pm != null)
            {
                if (pm.playerController != null) pm.playerController.enabled = true;
                try { pm.SetBipedIKEnabled(true); } catch { }
                try { pm.allowMovement(true); pm.setInputAllowed(!FreeCam.Active); } catch { }
            }
            Log.Msg("Player released from the pose builder.");
        }

        public static void Forget() { Active = false; hasGraph = false; sampling = false; Clip = null; }

        public static void LateTick()
        {
            if (!Active) return;
            try
            {
                var r = Root();
                if (r != null) r.SetPositionAndRotation(Pos, Rot);
                if (Clip == null) return;
                float len = Mathf.Max(0.05f, Clip.length), t = Time.time - clipStart;
                if (sampling) { var an = Anim(); if (an != null) Clip.SampleAnimation(an.gameObject, Loop ? t % len : Mathf.Min(t, len)); }
                else if (Loop && t > len && !Clip.isLooping) Play(Clip, Loop);
            }
            catch (Exception e) { Log.Warning("PlayerPose: " + e.Message); }
        }
    }

    /// <summary>
    /// Pose builder (F8): pick the player or a character, give them a solo animation and place them precisely
    /// (X / Y / Z + rotation, camera relative), to build your own scenes. Works with the free camera.
    /// </summary>
    internal static class Builder
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;
        public static bool Open { get; private set; }

        static int target;                 // 0 = player, 1.. = characters
        static int page;
        static string filter = "";
        static bool allClips;
        static List<AnimationClip> clips;
        static IntPtr clipsFor = IntPtr.Zero; static bool clipsAll;
        static readonly float[] moveSteps = { 0.01f, 0.05f, 0.25f };
        static readonly float[] turnSteps = { 1f, 5f, 15f, 90f };
        static int moveIdx = 1, turnIdx = 1;
        static Rect panel;

        public static void Toggle()
        {
            Open = !Open;
            if (Open) { clips = null; }
            Log.Msg("Pose builder " + (Open ? "open" : "closed"));
        }

        public static void Forget() { Open = false; clips = null; PlayerPose.Forget(); }

        static List<G.Character> Chars() => Puppets.Characters().ToList();

        static string TargetName()
        {
            if (target == 0) return "Player (you)";
            var cs = Chars();
            return target - 1 < cs.Count ? cs[target - 1].name : "?";
        }

        static G.Character TargetChar()
        {
            if (target == 0) return null;
            var cs = Chars();
            return target - 1 < cs.Count ? cs[target - 1] : null;
        }

        static Transform ViewCam()
        {
            try
            {
                var fc = FreeCam.CurrentCamera;
                if (fc != null) return fc.transform;
                var m = Camera.main; return m != null ? m.transform : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- moving the target
        static void Move(Vector3 local)   // x = right, y = up, z = forward (camera relative, flat)
        {
            var cam = ViewCam();
            Vector3 f = cam != null ? cam.forward : Vector3.forward; f.y = 0; if (f.sqrMagnitude < 1e-4f) f = Vector3.forward; f.Normalize();
            Vector3 r = new Vector3(f.z, 0, -f.x);
            Vector3 d = r * local.x + Vector3.up * local.y + f * local.z;
            if (target == 0) { PlayerPose.Begin(); PlayerPose.Pos += d; return; }
            var p = EnsurePuppet();
            if (p == null) return;
            p.Pos += d;
            if (Mathf.Abs(local.y) > 0) p.NoGround = true;       // manual height: stop snapping to the floor
        }

        static void Turn(float deg)
        {
            var q = Quaternion.Euler(0, deg, 0);
            if (target == 0) { PlayerPose.Begin(); PlayerPose.Rot = q * PlayerPose.Rot; return; }
            var p = EnsurePuppet();
            if (p != null) p.Rot = q * p.Rot;
        }

        static Puppet EnsurePuppet()
        {
            var c = TargetChar();
            if (c == null) return null;
            return Puppets.Get(c) ?? Puppets.Take(c, c.transform.position, c.transform.rotation);
        }

        static void PlayClip(AnimationClip clip)
        {
            if (target == 0) { PlayerPose.Play(clip, SandboxMod.LoopAnims); return; }
            var p = EnsurePuppet();
            if (p != null) Puppets.Play(p, clip, SandboxMod.LoopAnims);
        }

        static void BringTargetHere()
        {
            var cam = ViewCam();
            if (cam == null) return;
            Vector3 f = cam.forward; f.y = 0; f.Normalize();
            Vector3 pos = cam.position + f * 1.5f;
            if (UnityEngine.AI.NavMesh.SamplePosition(pos, out UnityEngine.AI.NavMeshHit hit, 3f, UnityEngine.AI.NavMesh.AllAreas)) pos = hit.position;
            var rot = Quaternion.LookRotation(-f);
            if (target == 0) { PlayerPose.Begin(); PlayerPose.Pos = pos; PlayerPose.Rot = rot; return; }
            var c = TargetChar(); if (c == null) return;
            var p = Puppets.Take(c, pos, rot);
            p.Pos = pos; p.Rot = rot;
        }

        // ---------------------------------------------------------------- keys (numpad)
        public static void Tick()
        {
            if (!Open) return;
            float m = moveSteps[moveIdx], t = turnSteps[turnIdx];
            if (Input.GetKeyDown(KeyCode.Keypad4)) Move(new Vector3(-m, 0, 0));
            if (Input.GetKeyDown(KeyCode.Keypad6)) Move(new Vector3(m, 0, 0));
            if (Input.GetKeyDown(KeyCode.Keypad8)) Move(new Vector3(0, 0, m));
            if (Input.GetKeyDown(KeyCode.Keypad2)) Move(new Vector3(0, 0, -m));
            if (Input.GetKeyDown(KeyCode.Keypad9)) Move(new Vector3(0, m, 0));
            if (Input.GetKeyDown(KeyCode.Keypad3)) Move(new Vector3(0, -m, 0));
            if (Input.GetKeyDown(KeyCode.Keypad7)) Turn(-t);
            if (Input.GetKeyDown(KeyCode.Keypad1)) Turn(t);
            if (Input.GetKeyDown(KeyCode.KeypadPlus)) moveIdx = Mathf.Min(moveSteps.Length - 1, moveIdx + 1);
            if (Input.GetKeyDown(KeyCode.KeypadMinus)) moveIdx = Mathf.Max(0, moveIdx - 1);
            if (!FreeCam.Active) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        public static bool MouseOver()
        {
            if (!Open) return false;
            Vector2 mp = Input.mousePosition; mp.y = Screen.height - mp.y;
            return panel.Contains(mp);
        }

        // ---------------------------------------------------------------- UI
        public static void Draw()
        {
            if (!Open) return;
            try
            {
                panel = new Rect(20, 60, 470, Mathf.Min(Screen.height - 100, 760));
                GUI.Box(panel, ""); GUI.Box(panel, "");
                GUI.Box(new Rect(panel.x, panel.y, panel.width, 26), "Pose builder — F8");
                float x = panel.x + 10, w = panel.width - 20, y = panel.y + 32;

                // target
                int count = 1 + Chars().Count;
                GUI.Label(new Rect(x, y + 4, 60, 22), "Target:");
                if (GUI.Button(new Rect(x + 60, y, 30, 26), "◀")) { target = (target - 1 + count) % count; clips = null; page = 0; }
                GUI.Label(new Rect(x + 96, y + 4, 170, 22), TargetName());
                if (GUI.Button(new Rect(x + 266, y, 30, 26), "▶")) { target = (target + 1) % count; clips = null; page = 0; }
                if (GUI.Button(new Rect(x + 304, y, w - 304, 26), "Bring in front of camera")) BringTargetHere();
                y += 32;

                // status + release
                string st;
                if (target == 0) st = PlayerPose.Active ? $"frozen · {(PlayerPose.Clip != null ? PlayerPose.Clip.name : "no anim")}" : "free";
                else { var p = TargetChar() != null ? Puppets.Get(TargetChar()) : null; st = p != null ? $"frozen · {(p.Clip != null ? p.Clip.name : "no anim")}" : "free"; }
                GUI.Label(new Rect(x, y + 4, w - 200, 22), "State: " + st);
                if (GUI.Button(new Rect(x + w - 196, y, 96, 26), "Stop anim"))
                {
                    if (target == 0) PlayerPose.StopClip(); else { var p = TargetChar() != null ? Puppets.Get(TargetChar()) : null; if (p != null) Puppets.StopClip(p); }
                }
                if (GUI.Button(new Rect(x + w - 96, y, 96, 26), "Release"))
                {
                    if (target == 0) PlayerPose.End(false); else { var p = TargetChar() != null ? Puppets.Get(TargetChar()) : null; if (p != null) Puppets.Release(p); }
                }
                y += 34;

                // steps
                GUI.Label(new Rect(x, y + 4, 60, 22), "Step:");
                for (int i = 0; i < moveSteps.Length; i++)
                    if (GUI.Button(new Rect(x + 60 + i * 62, y, 58, 24), (moveIdx == i ? "▶" : "") + (moveSteps[i] * 100f).ToString("0") + " cm")) moveIdx = i;
                for (int i = 0; i < turnSteps.Length; i++)
                    if (GUI.Button(new Rect(x + 254 + i * 50, y, 46, 24), (turnIdx == i ? "▶" : "") + turnSteps[i].ToString("0") + "°")) turnIdx = i;
                y += 30;

                // move pad (camera relative)
                float m = moveSteps[moveIdx], t = turnSteps[turnIdx];
                float bx = x;
                if (GUI.Button(new Rect(bx, y, 64, 28), "◀ Left")) Move(new Vector3(-m, 0, 0)); bx += 66;
                if (GUI.Button(new Rect(bx, y, 64, 28), "Right ▶")) Move(new Vector3(m, 0, 0)); bx += 70;
                if (GUI.Button(new Rect(bx, y, 64, 28), "▲ Fwd")) Move(new Vector3(0, 0, m)); bx += 66;
                if (GUI.Button(new Rect(bx, y, 64, 28), "▼ Back")) Move(new Vector3(0, 0, -m)); bx += 70;
                if (GUI.Button(new Rect(bx, y, 64, 28), "Up")) Move(new Vector3(0, m, 0)); bx += 66;
                if (GUI.Button(new Rect(bx, y, 64, 28), "Down")) Move(new Vector3(0, -m, 0));
                y += 32;
                if (GUI.Button(new Rect(x, y, 110, 28), $"⟲ {t:0}°")) Turn(-t);
                if (GUI.Button(new Rect(x + 114, y, 110, 28), $"⟳ {t:0}°")) Turn(t);
                if (target != 0)
                {
                    var p = TargetChar() != null ? Puppets.Get(TargetChar()) : null;
                    if (p != null) p.NoGround = !GUI.Toggle(new Rect(x + 236, y + 5, 150, 22), !p.NoGround, " Snap to floor");
                }
                y += 32;
                GUI.Label(new Rect(x, y, w, 40), "Numpad: 4/6 left-right · 8/2 fwd-back · 9/3 up-down · 7/1 turn · +/- step\nDirections follow the camera (works in free cam, F6).");
                y += 44;

                // animations
                SandboxMod.LoopAnims = GUI.Toggle(new Rect(x, y, 80, 22), SandboxMod.LoopAnims, " Loop");
                bool all = GUI.Toggle(new Rect(x + 84, y, 200, 22), allClips, " All game animations");
                if (all != allClips) { allClips = all; clips = null; page = 0; }
                y += 26;
                GUI.Label(new Rect(x, y, 50, 24), "Filter:");
                string nf = GUI.TextField(new Rect(x + 50, y, w - 50, 24), filter ?? "");
                if (nf != filter) { filter = nf; page = 0; }
                y += 30;

                IntPtr key = target == 0 ? new IntPtr(1) : (TargetChar() != null ? TargetChar().Pointer : IntPtr.Zero);
                if (clips == null || clipsFor != key || clipsAll != allClips)
                {
                    clipsFor = key; clipsAll = allClips;
                    if (allClips) clips = Puppets.AllLoadedClips();
                    else if (target == 0) clips = PlayerClips();
                    else clips = TargetChar() != null ? Puppets.ClipsOf(TargetChar()) : new List<AnimationClip>();
                }
                var list = string.IsNullOrEmpty(filter) ? clips : clips.Where(c => c != null && c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                int per = Mathf.Max(4, (int)((panel.yMax - y - 40) / 24f));
                int pages = Mathf.Max(1, (list.Count + per - 1) / per);
                page = Mathf.Clamp(page, 0, pages - 1);
                foreach (var c in list.Skip(page * per).Take(per))
                {
                    if (c == null) continue;
                    if (GUI.Button(new Rect(x, y, w, 22), $"{c.name}  ({c.length:0.0}s)")) PlayClip(c);
                    y += 24;
                }
                float by = panel.yMax - 32;
                if (GUI.Button(new Rect(x, by, 50, 24), "◀")) page = Mathf.Max(0, page - 1);
                GUI.Label(new Rect(x + 60, by + 2, w - 120, 22), $"page {page + 1}/{pages} — {list.Count} anims");
                if (GUI.Button(new Rect(panel.xMax - 60, by, 50, 24), "▶")) page = Mathf.Min(pages - 1, page + 1);
            }
            catch (Exception e) { Log.Warning("Builder.Draw: " + e.Message); }
        }

        static List<AnimationClip> PlayerClips()
        {
            var res = new List<AnimationClip>();
            try
            {
                var an = PlayerPose.Anim();
                var rac = an != null ? an.runtimeAnimatorController : null;
                if (rac != null) foreach (var c in rac.animationClips) if (c != null && !res.Any(x => x.Pointer == c.Pointer)) res.Add(c);
            }
            catch { }
            return res.OrderBy(c => c.name).ToList();
        }
    }
}
