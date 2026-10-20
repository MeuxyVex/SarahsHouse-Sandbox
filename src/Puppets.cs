using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Playables;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>A character taken over by the sandbox: frozen in place, game AI off, optional custom clip.</summary>
    internal class Puppet
    {
        public G.Character Ch;
        public string Name;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool CharWasEnabled = true;
        public bool AnimatorWasEnabled = true;
        public bool RootMotionWas;

        public AnimationClip Clip;
        public bool Loop = true;
        public float ClipStart;
        public bool HasGraph;
        public PlayableGraph Graph;
        public bool Sampling;          // fallback mode: AnimationClip.SampleAnimation every frame
        public float ManualY;          // user height adjustment (m)
        public float AutoY;            // automatic ground snapping offset (m)
        public bool NoGround;          // exact positions (two-person scenes): never snap to the floor
    }

    internal static class Puppets
    {
        public static readonly List<Puppet> All = new List<Puppet>();
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        static int playClipOk = -1;   // -1 unknown, 0 stripped, 1 available
        public static bool AutoGround = true;
        static readonly HumanBodyBones[] groundBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        };

        public static Puppet Get(G.Character c)
        {
            if (c == null) return null;
            foreach (var p in All) if (p.Ch != null && p.Ch.Pointer == c.Pointer) return p;
            return null;
        }

        public static IEnumerable<G.Character> Characters()
        {
            var gm = G.GameManager.INSTANCE;
            if (gm == null || gm.characterList == null) yield break;
            foreach (var c in gm.characterList)
                if (c != null) yield return c;
        }

        // -------------------------------------------------------------- take / release
        public static Puppet Take(G.Character c, Vector3 pos, Quaternion rot, bool idle = true)
        {
            var p = Get(c);
            if (p == null)
            {
                p = new Puppet { Ch = c, Name = c.name };
                try { p.CharWasEnabled = c.enabled; } catch { }
                var anim = Anim(c);
                if (anim != null) { p.AnimatorWasEnabled = anim.enabled; p.RootMotionWas = anim.applyRootMotion; }
                All.Add(p);
            }
            p.Pos = pos; p.Rot = rot;
            try
            {
                if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                c.enabled = false;                 // stops schedule/AI/feet IK of this character
                var agent = c.GetComponent<NavMeshAgent>();
                if (agent != null) agent.enabled = false;
                var anim = Anim(c);
                if (anim != null) { anim.applyRootMotion = false; anim.enabled = true; }
                c.transform.SetPositionAndRotation(pos, rot);
            }
            catch (Exception e) { Log.Warning($"Take {p.Name}: {e.Message}"); }

            if (idle && p.Clip == null) PlayIdle(p);
            Log.Msg($"Character frozen: {p.Name}");
            return p;
        }

        public static void Release(Puppet p)
        {
            StopClip(p);
            try
            {
                var c = p.Ch;
                if (c != null)
                {
                    var anim = Anim(c);
                    if (anim != null) { anim.applyRootMotion = p.RootMotionWas; anim.enabled = p.AnimatorWasEnabled; }
                    var agent = c.GetComponent<NavMeshAgent>();
                    if (agent != null) agent.enabled = true;
                    c.enabled = p.CharWasEnabled;
                    try { c.ResetAction(); } catch (Exception e) { Log.Warning($"ResetAction {p.Name}: {e.Message}"); }
                    Clothes.Reapply(c);
                }
            }
            catch (Exception e) { Log.Warning($"Release {p.Name}: {e.Message}"); }
            All.Remove(p);
            Log.Msg($"Character released: {p.Name}");
        }

        public static void ReleaseAll()
        {
            foreach (var p in All.ToList()) Release(p);
        }

        /// <summary>Forget everything without touching game objects (scene unloaded).</summary>
        public static void Forget() { All.Clear(); }

        /// <summary>Places every given character in a half circle in front of the player, facing him.</summary>
        public static void BringInFront(List<G.Character> chars, float radiusOverride = -1f)
        {
            var pm = G.PlayerManager.instance;
            if (pm == null || chars.Count == 0) return;
            Transform pt = pm.playerController != null ? pm.playerController.transform : pm.transform;
            Vector3 fwd = pt.forward; fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
            Vector3 center = pt.position;
            float radius = radiusOverride > 0 ? radiusOverride : 1.8f + 0.15f * chars.Count;
            float spread = Mathf.Min(150f, 30f * (chars.Count - 1));
            for (int i = 0; i < chars.Count; i++)
            {
                float a = chars.Count == 1 ? 0f : -spread / 2f + spread * i / (chars.Count - 1);
                Vector3 dir = Quaternion.Euler(0, a, 0) * fwd;
                Vector3 pos = center + dir * radius;
                pos = Ground(pos, center.y);
                Vector3 look = center - pos; look.y = 0;
                Take(chars[i], pos, Quaternion.LookRotation(look.sqrMagnitude > 0.001f ? look : -dir));
            }
        }

        static Vector3 Ground(Vector3 pos, float fallbackY)
        {
            try
            {
                if (NavMesh.SamplePosition(pos, out NavMeshHit hit, 1.5f, NavMesh.AllAreas)) return hit.position;
            }
            catch { }
            pos.y = fallbackY;
            return pos;
        }

        // -------------------------------------------------------------- per-frame enforcement
        public static void LateTick()
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var p = All[i];
                try
                {
                    var c = p.Ch;
                    if (c == null) { All.RemoveAt(i); continue; }
                    if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                    if (c.enabled) c.enabled = false;
                    var agent = c.GetComponent<NavMeshAgent>();
                    if (agent != null && agent.enabled) agent.enabled = false;
                    if (!AutoGround || p.Clip == null || p.NoGround) p.AutoY = Mathf.Lerp(p.AutoY, 0f, 0.3f);
                    c.transform.SetPositionAndRotation(p.Pos + Vector3.up * (p.ManualY + p.AutoY), p.Rot);

                    if (p.Clip != null)
                    {
                        float len = Mathf.Max(0.05f, p.Clip.length);
                        float t = Time.time - p.ClipStart;
                        if (p.Sampling)
                        {
                            float st = p.Loop ? t % len : Mathf.Min(t, len);
                            p.Clip.SampleAnimation(c.gameObject, st);
                        }
                        else if (p.Loop && t > len && !p.Clip.isLooping)
                        {
                            Play(p, p.Clip, p.Loop);   // restart non looping clips
                        }
                    }
                    try { if (AutoGround && p.Clip != null && !p.NoGround) UpdateGround(p); } catch { }
                }
                catch (Exception e) { Log.Warning($"LateTick {p.Name}: {e.Message}"); All.RemoveAt(i); }
            }
        }

        /// <summary>Moves the character up/down so the lowest bone of the current pose touches the floor.</summary>
        static void UpdateGround(Puppet p)
        {
            var anim = Anim(p.Ch);
            if (anim == null || !anim.isHuman) return;
            float min = float.MaxValue;
            foreach (var b in groundBones)
            {
                var t = anim.GetBoneTransform(b);
                if (t != null && t.position.y < min) min = t.position.y;
            }
            if (min == float.MaxValue) return;
            float desiredLowest = p.Pos.y + p.ManualY + 0.06f;      // joints sit a few cm above the skin
            float target = p.AutoY + (desiredLowest - min);
            p.AutoY = Mathf.Lerp(p.AutoY, Mathf.Clamp(target, -2f, 2f), 0.25f);
        }

        // -------------------------------------------------------------- animations
        public static Animator Anim(G.Character c)
        {
            try { var a = c.GetAnimator(); if (a != null) return a; } catch { }
            try { return c.GetComponentInChildren<Animator>(); } catch { return null; }
        }

        public static List<AnimationClip> ClipsOf(G.Character c)
        {
            var res = new List<AnimationClip>();
            try
            {
                var a = Anim(c);
                var rac = a != null ? a.runtimeAnimatorController : null;
                if (rac != null)
                    foreach (var cl in rac.animationClips)
                        if (cl != null && !res.Any(x => x.Pointer == cl.Pointer)) res.Add(cl);
            }
            catch (Exception e) { Log.Warning("ClipsOf: " + e.Message); }
            return res.OrderBy(x => x.name).ToList();
        }

        public static List<AnimationClip> AllLoadedClips()
        {
            var res = new List<AnimationClip>();
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll<AnimationClip>())
                    if (o != null && !string.IsNullOrEmpty(o.name)) res.Add(o);
            }
            catch (Exception e) { Log.Warning("AllLoadedClips: " + e.Message); }
            return res.GroupBy(x => x.name).Select(g => g.First()).OrderBy(x => x.name).ToList();
        }

        public static void PlayIdle(Puppet p)
        {
            var clips = ClipsOf(p.Ch);
            AnimationClip idle =
                clips.FirstOrDefault(c => c.name.Equals("Idle", StringComparison.OrdinalIgnoreCase)) ??
                clips.FirstOrDefault(c => c.name.StartsWith("Idle", StringComparison.OrdinalIgnoreCase) && !c.name.ToLower().Contains("sit")) ??
                clips.FirstOrDefault(c => c.name.ToLower().Contains("idle") && !c.name.ToLower().Contains("sit") && !c.name.ToLower().Contains("lay"));
            if (idle == null)
            {
                // Some controllers don't reference a plain idle: look among every loaded clip.
                idle = AllLoadedClips().FirstOrDefault(c => c.name.Equals("Idle", StringComparison.OrdinalIgnoreCase));
            }
            if (idle != null) Play(p, idle, true);
            else Log.Msg($"No idle found for {p.Name}, keeping the current animation.");
        }

        public static void Play(Puppet p, AnimationClip clip, bool loop)
        {
            StopClip(p);
            p.Clip = clip; p.Loop = loop; p.ClipStart = Time.time;
            var anim = Anim(p.Ch);
            if (anim == null) { Log.Warning($"{p.Name}: no Animator"); return; }

            if (playClipOk < 0)
            {
                playClipOk = (HasNative(typeof(AnimationPlayableUtilities), "PlayClip") ? 1 : 0);
                Log.Msg($"Playables PlayClip disponible: {playClipOk == 1}");
            }

            if (playClipOk == 1)
            {
                try
                {
                    anim.enabled = true;
                    PlayableGraph graph;
                    AnimationPlayableUtilities.PlayClip(anim, clip, out graph);
                    p.Graph = graph; p.HasGraph = true; p.Sampling = false;
                    Log.Msg($"{p.Name} joue '{clip.name}' ({clip.length:0.0}s, loop={loop})");
                    return;
                }
                catch (Exception e) { Log.Warning("PlayClip failed, switching to sampling mode: " + e.Message); playClipOk = 0; }
            }

            // Fallback: drive the pose ourselves each frame.
            anim.enabled = false;
            p.Sampling = true;
            Log.Msg($"{p.Name} plays '{clip.name}' (sampling mode)");
        }

        /// <summary>Plays a clip on any animator through Playables. False = not available (use sampling).</summary>
        public static bool TryPlayGraph(Animator anim, AnimationClip clip, out PlayableGraph graph)
        {
            graph = default;
            if (playClipOk < 0) playClipOk = HasNative(typeof(AnimationPlayableUtilities), "PlayClip") ? 1 : 0;
            if (playClipOk != 1 || anim == null || clip == null) return false;
            try { anim.enabled = true; AnimationPlayableUtilities.PlayClip(anim, clip, out graph); return true; }
            catch (Exception e) { Log.Warning("PlayClip: " + e.Message); playClipOk = 0; return false; }
        }

        public static void StopClip(Puppet p)
        {
            if (p.HasGraph)
            {
                try { p.Graph.Destroy(); } catch (Exception e) { Log.Warning("Graph.Destroy: " + e.Message); }
                p.HasGraph = false;
            }
            if (p.Sampling)
            {
                p.Sampling = false;
                try { var a = Anim(p.Ch); if (a != null) a.enabled = true; } catch { }
            }
            p.Clip = null;
        }

        /// <summary>True if the il2cpp method behind a proxy method still has native code (not stripped).</summary>
        static bool HasNative(Type t, string methodName)
        {
            try
            {
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!f.Name.StartsWith("NativeMethodInfoPtr_" + methodName + "_")) continue;
                    var info = (IntPtr)f.GetValue(null);
                    if (info == IntPtr.Zero) return false;
                    return Marshal.ReadIntPtr(info) != IntPtr.Zero;   // MethodInfo->methodPointer
                }
            }
            catch (Exception e) { Log.Warning("HasNative: " + e.Message); }
            return false;
        }

        // -------------------------------------------------------------- picking
        /// <summary>Character closest to a screen ray (no physics needed).</summary>
        public static G.Character Pick(Ray ray, float maxDist = 25f)
        {
            G.Character best = null; float bestScore = 0.7f;
            foreach (var c in Characters())
            {
                try
                {
                    if (!c.gameObject.activeInHierarchy) continue;
                    Vector3 chest = c.transform.position + Vector3.up * 1.1f;
                    Vector3 v = chest - ray.origin;
                    float along = Vector3.Dot(v, ray.direction);
                    if (along < 0.2f || along > maxDist) continue;
                    float off = Vector3.Cross(ray.direction, v).magnitude; // distance to ray
                    float score = off / Mathf.Max(1f, along * 0.25f);
                    if (score < bestScore) { bestScore = score; best = c; }
                }
                catch { }
            }
            return best;
        }
    }
}
