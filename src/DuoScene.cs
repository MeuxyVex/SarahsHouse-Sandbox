using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Replays one of the game's two-person scenes *in place* (no scene/save change):
    /// positions, player + character animations (always automatic, looping), sounds.
    /// A role can be played by another (humanoid) character: the original character's clips are
    /// retargeted onto them. Stop at any time; everyone goes back where they were.
    /// </summary>
    internal static class DuoScene
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        class AnimSpec
        {
            public G.Character.Characters Role;
            public string Start, Loop;               // Loop null = play once and hold
            public List<string> Extras = new List<string>();
        }

        class Step
        {
            public G.LovePose Pose;
            public int Index;
            public string Label;
            public float AnimSpeed = 1f;
            public G.SexSound Sound = G.SexSound.NONE;
            public bool Finisher;
            public G.LoveScene Placement;            // scene whose positions apply to this step
            public List<AnimSpec> Anims = new List<AnimSpec>();
        }

        static Step FromPoseStep(G.LovePose pose, G.PoseStep st, int i)
        {
            var step = new Step { Pose = pose, Index = i, Label = $"{pose.name} · {i + 1}" };
            try { step.AnimSpeed = st.animSpeed > 0.01f ? st.animSpeed : 1f; } catch { }
            try { step.Sound = st.sexSound; } catch { }
            foreach (var pc in st.charactersAnims)
            {
                if (pc == null) continue;
                var spec = new AnimSpec { Role = pc.character };
                string loop = !string.IsNullOrEmpty(pc.loopAnim) ? pc.loopAnim : pc.idleAnim;
                spec.Loop = string.IsNullOrEmpty(loop) ? null : loop;
                spec.Start = !string.IsNullOrEmpty(pc.startAnim) && pc.startAnim != spec.Loop ? pc.startAnim : null;
                try { if (pc.additionalAnimationsOnStart != null) foreach (var e in pc.additionalAnimationsOnStart) if (!string.IsNullOrEmpty(e)) spec.Extras.Add(e); } catch { }
                try { if (pc.additionalAnomsOnLoop != null) foreach (var e in pc.additionalAnomsOnLoop) if (!string.IsNullOrEmpty(e)) spec.Extras.Add(e); } catch { }
                step.Anims.Add(spec);
            }
            return step;
        }

        /// <summary>The pose's ending (finisher) as a last step: played once and held.</summary>
        static Step FinisherStep(G.LovePose pose, G.SexSound sound)
        {
            try
            {
                var fin = pose.finisher;
                if (fin == null || fin.anims == null || fin.anims.Length == 0) return null;
                var step = new Step { Pose = pose, Label = $"{pose.name} · end", Finisher = true, Sound = sound };
                foreach (var fa in fin.anims)
                {
                    if (fa == null || string.IsNullOrEmpty(fa.anim)) continue;
                    var spec = new AnimSpec { Role = fa.character, Start = fa.anim, Loop = null };
                    try { if (fa.additionalAnims != null) foreach (var e in fa.additionalAnims) if (!string.IsNullOrEmpty(e)) spec.Extras.Add(e); } catch { }
                    step.Anims.Add(spec);
                }
                return step.Anims.Count > 0 ? step : null;
            }
            catch { return null; }
        }

        class Actor
        {
            public bool IsPlayer;
            public G.Character.Characters Role;      // role in the scene (Player, Sarah, Lucy...)
            public G.Character Ch;                    // who actually plays it (may be swapped)
            public Animator Anim;
            public Vector3 Pos; public Quaternion Rot;
            public Vector3 BasePos; public Quaternion BaseRot;     // placement from the scene data, before user adjustment
            public bool ClipMode;                     // swapped: play retargeted clips instead of animator states
            // saved state, restored when leaving the scene
            public bool WasPuppet; public Vector3 SavedPos; public Quaternion SavedRot; public AnimationClip SavedClip;
            // current step playback
            public string StartName, LoopName, BaseLoop;
            public bool InStart;
            public float PhaseStart;                  // Time.time when the current start/loop began (clip mode)
            public AnimationClip CurClip;
            // transposed girl: rig nodes above the hips keep their own rest pose (the source clip's values don't fit)
            public List<Transform> UpperNodes = new List<Transform>();
            public List<Vector3> UpperPos = new List<Vector3>();       // target girl rest values
            public List<Quaternion> UpperRot = new List<Quaternion>();
            public List<Vector3> UpperScale = new List<Vector3>();
            public List<Vector3> SrcPos = new List<Vector3>();         // original girl rest values (same node names)
            public List<Quaternion> SrcRot = new List<Quaternion>();
            public List<Vector3> LastPos = new List<Vector3>();        // what we wrote last frame
            public List<Quaternion> LastRot = new List<Quaternion>();
        }

        public static bool Active => scene != null;
        public static bool MouseFree = true;
        static G.LoveScene scene;
        static readonly List<Step> steps = new List<Step>();
        static readonly List<Actor> actors = new List<Actor>();
        static int cur;
        static float stepStart;
        static bool auto = true;
        static float loopSeconds = 12f;
        static float speed = 1f;
        static int rhythm = 1;                 // Loop1 / Loop2 / Loop3 variants of a loop (game speed levels)
        static float nextSound, nextMoan;
        static Vector3 plSavedPos; static Quaternion plSavedRot;
        static readonly System.Random rng = new System.Random();
        static string info = "";
        static readonly Dictionary<string, Dictionary<string, AnimationClip>> clipCache = new Dictionary<string, Dictionary<string, AnimationClip>>();

        // ------------------------------------------------------------------ start
        public static void Start(G.LoveScene sc)
        {
            if (Active) Stop();
            try
            {
                var pm = G.PlayerManager.instance;
                if (pm == null || sc == null) return;
                steps.Clear(); actors.Clear();
                BuildSteps(sc);
                if (steps.Count == 0) { Log.Warning("Scene without playable steps: " + LoveScenes.Title(sc)); return; }
                scene = sc;
                MouseFree = true;
                adjTarget = 0;
                placedBy = sc;

                // Player
                var pt = PlayerRoot();
                plSavedPos = pt.position; plSavedRot = pt.rotation;
                var pa = new Actor { IsPlayer = true, Role = G.Character.Characters.Player, Anim = pm.GetAnimator(), Pos = sc.playerPos, Rot = sc.playerRot };
                actors.Add(pa);
                SandboxMod.I.BlockPlayer(true);
                try { pm.allowMovement(false); } catch { }
                if (pm.playerController != null) pm.playerController.enabled = false;
                try { pm.EnableIK(false); } catch { }
                try { pm.SetPenisEnabled(true); } catch { }
                pt.SetPositionAndRotation(pa.Pos, pa.Rot);
                PrepareForScene(pa.Anim, null);   // player: mute override layers only; its look/IK scripts stay on (the game relies on them)

                // Characters
                if (sc.characters != null)
                {
                    foreach (var lc in sc.characters)
                    {
                        if (lc == null) continue;
                        var c = FindCharacter(lc.character);
                        if (c == null) { Log.Warning($"Character missing: {lc.character}"); continue; }
                        var a = new Actor { Role = lc.character, Pos = lc.initPos, Rot = lc.initRot };
                        Cast(a, c);
                        actors.Add(a);
                    }
                }

                foreach (var a in actors) { a.BasePos = a.Pos; a.BaseRot = a.Rot; }
                ApplyAdjust();
                cur = 0;
                PlayStep(0);
                Log.Msg($"Two-person scene in place: {LoveScenes.Title(sc)} ({steps.Count} steps, {actors.Count} participants)");
            }
            catch (Exception e) { Log.Error("DuoScene.Start: " + e); Stop(); }
        }

        /// <summary>Puts character c in the actor's role (saves c's current state to restore it later).</summary>
        static void Cast(Actor a, G.Character c)
        {
            a.Ch = c;
            var existing = Puppets.Get(c);
            a.WasPuppet = existing != null;
            a.SavedPos = existing != null ? existing.Pos : c.transform.position;
            a.SavedRot = existing != null ? existing.Rot : c.transform.rotation;
            a.SavedClip = existing?.Clip;
            var p = Puppets.Take(c, a.Pos, a.Rot, false);
            Puppets.StopClip(p);
            p.ManualY = 0f; p.AutoY = 0f; p.NoGround = true;
            a.Anim = Puppets.Anim(c);
            a.ClipMode = !string.Equals(c.name, a.Role.ToString(), StringComparison.OrdinalIgnoreCase);
            if (!a.ClipMode) RememberRoleRest(a.Role, c);
            PrepareForScene(a.Anim, c.gameObject);
            CaptureUpperNodes(a);
            Clothes.Reapply(c);
        }

        static Transform PlayerRoot()
        {
            var pm = G.PlayerManager.instance;
            return pm.playerController != null ? pm.playerController.transform : pm.transform;
        }

        static G.Character FindCharacter(G.Character.Characters id) => FindCharacter(id.ToString());
        static G.Character FindCharacter(string n)
        {
            foreach (var c in Puppets.Characters())
                if (string.Equals(c.name, n, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>Walks the starting pose and every pose reachable from it.</summary>
        static void BuildSteps(G.LoveScene sc)
        {
            var queue = new Queue<G.LovePose>();
            var seen = new HashSet<IntPtr>();
            var placement = new Dictionary<IntPtr, G.LoveScene>();
            G.LoveScene from = sc;
            void Enq(G.LovePose lp)
            {
                if (lp == null || !seen.Add(lp.Pointer)) return;
                // a pose that starts another (real) scene uses that scene's placements, otherwise the parent's
                placement[lp.Pointer] = LoveScenes.OwnerOf(lp) ?? from;
                queue.Enqueue(lp);
            }
            Enq(sc.startingPose);
            placement[sc.startingPose.Pointer] = sc;
            try { if (sc.dynamicStartingPoses != null) foreach (var d in sc.dynamicStartingPoses) if (d != null) Enq(d.pose); } catch { }
            while (queue.Count > 0)
            {
                var pose = queue.Dequeue();
                from = placement.TryGetValue(pose.Pointer, out var pl) ? pl : sc;
                int firstStep = steps.Count;
                var ps = pose.poseSteps;
                if (ps != null)
                {
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var st = ps[i];
                        if (st == null || st.charactersAnims == null || st.charactersAnims.Length == 0) continue;
                        steps.Add(FromPoseStep(pose, st, i));
                        try { if (st.transition != null && st.transition.interaction != null) Enq(st.transition.interaction.pose); } catch { }
                        try { if (st.localInteractions != null) foreach (var li in st.localInteractions) if (li != null) Enq(li.pose); } catch { }
                    }
                }
                var fin = FinisherStep(pose, steps.Count > 0 ? steps[steps.Count - 1].Sound : G.SexSound.NONE);
                if (fin != null) steps.Add(fin);
                for (int k = firstStep; k < steps.Count; k++) steps[k].Placement = from;
                try { Enq(pose.nextScene != null ? pose.nextScene.startingPose : null); } catch { }
                try { if (pose.poseGlobalInteractions != null) foreach (var gi in pose.poseGlobalInteractions) if (gi != null) Enq(gi.pose); } catch { }
                try { Enq(pose.nextPose); } catch { }
                try { Enq(pose.manualSelectablePose); } catch { }
            }
        }

        // ------------------------------------------------------------------ swapping
        /// <summary>Female humanoid characters that can take a role.</summary>
        /// <summary>The four main girls share the same rig, so their animations can be swapped between them.</summary>
        static readonly string[] swappable = { "Lucy", "Lisa", "Sarah", "Mia" };

        public static List<G.Character> Candidates()
        {
            var res = new List<G.Character>();
            foreach (var n in swappable)
            {
                var c = FindCharacter(n);
                if (c != null) res.Add(c);
            }
            return res;
        }

        static void SwapRole(Actor a, int dir)
        {
            var list = Candidates();
            if (list.Count < 2) return;
            int idx = list.FindIndex(c => c.Pointer == a.Ch.Pointer);
            G.Character next = null;
            for (int k = 1; k <= list.Count; k++)
            {
                var cand = list[((idx + dir * k) % list.Count + list.Count) % list.Count];
                if (actors.Any(o => o != a && o.Ch != null && o.Ch.Pointer == cand.Pointer)) continue;   // already in the scene
                next = cand; break;
            }
            if (next == null || next.Pointer == a.Ch.Pointer) return;
            RestoreActor(a);
            Cast(a, next);
            Log.Msg($"Role {a.Role} played by {next.name}{(a.ClipMode ? " (retargeted animations)" : "")}");
            PlayStep(cur);
        }

        /// <summary>Clips of the character that originally plays the role, by name.</summary>
        static Dictionary<string, AnimationClip> RoleClips(G.Character.Characters role)
        {
            string key = role.ToString();
            if (clipCache.TryGetValue(key, out var d)) return d;
            d = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
            var src = FindCharacter(role);
            if (src != null) foreach (var cl in Puppets.ClipsOf(src)) if (cl != null && !d.ContainsKey(cl.name)) d[cl.name] = cl;
            clipCache[key] = d;
            return d;
        }

        static AnimationClip ResolveClip(G.Character.Characters role, string state)
        {
            if (string.IsNullOrEmpty(state)) return null;
            var d = RoleClips(role);
            if (d.TryGetValue(state, out var c)) return c;
            // state and clip names usually match; otherwise take the closest name
            AnimationClip best = null; int bestLen = int.MaxValue;
            foreach (var kv in d)
            {
                if (kv.Key.IndexOf(state, StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int diff = Math.Abs(kv.Key.Length - state.Length);
                    if (diff < bestLen) { bestLen = diff; best = kv.Value; }
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ playback
        static Actor ActorFor(G.Character.Characters id) => actors.FirstOrDefault(a => a.Role == id);

        static bool HasState(Animator an, string state)
        {
            try { return an != null && !string.IsNullOrEmpty(state) && an.HasState(0, Animator.StringToHash(state)); }
            catch { return false; }
        }

        static void PlayStep(int i)
        {
            if (steps.Count == 0) return;
            cur = (i % steps.Count + steps.Count) % steps.Count;
            var st = steps[cur];
            stepStart = Time.time;
            nextSound = Time.time + 0.5f; nextMoan = Time.time + 2f;
            foreach (var a in actors) { a.StartName = a.LoopName = null; a.InStart = false; a.CurClip = null; }
            foreach (var e in extras) { try { e.A.Anim.SetLayerWeight(e.Layer, 0f); } catch { } }
            extras.Clear();

            ApplyPlacement(st.Placement);
            foreach (var spec in st.Anims)
            {
                var a = ActorFor(spec.Role);
                if (a == null || a.Anim == null) continue;
                a.StartName = spec.Start;
                a.BaseLoop = spec.Loop;
                a.LoopName = Variant(a, spec.Loop);
                a.InStart = a.StartName != null && a.LoopName != null;
                Begin(a, a.StartName ?? a.LoopName, 0.25f);
                // extra layered animations of the step (hands, face...)
                // Layered extras (face anims) are not played: their override layer freezes the whole body here.
            }
            ApplySpeed();
            info = $"Step {cur + 1}/{steps.Count}: {st.Label}";
            Log.Msg("Duo · " + info);
        }

        static readonly Dictionary<IntPtr, HashSet<string>> paramCache = new Dictionary<IntPtr, HashSet<string>>();
        static bool HasParam(Animator an, string name)
        {
            try
            {
                if (!paramCache.TryGetValue(an.Pointer, out var set))
                {
                    set = new HashSet<string>();
                    foreach (var p in an.parameters) if (p != null) set.Add(p.name);
                    paramCache[an.Pointer] = set;
                }
                return set.Contains(name);
            }
            catch { return false; }
        }

        /// <summary>Loop1 -> Loop2/Loop3 according to the chosen rhythm, when that variant exists.</summary>
        static string Variant(Actor a, string loop)
        {
            if (loop == null || rhythm <= 1 || loop.IndexOf("Loop1", StringComparison.Ordinal) < 0) return loop;
            string cand = loop.Replace("Loop1", "Loop" + rhythm);
            bool ok = a.ClipMode ? ResolveClip(a.Role, cand) != null : HasState(a.Anim, cand);
            return ok ? cand : loop;
        }

        static void SetRhythm(int r)
        {
            rhythm = Mathf.Clamp(r, 1, 3);
            foreach (var a in actors)
            {
                if (a.BaseLoop == null) continue;
                a.LoopName = Variant(a, a.BaseLoop);
                if (!a.InStart) Begin(a, a.LoopName, 0.15f);
            }
            Log.Msg("Duo · rhythm " + rhythm);
        }

        // Rest pose of the rig nodes (hips and above) of each role's original girl, taken before the scene plays.
        static readonly Dictionary<string, Dictionary<string, KeyValuePair<Vector3, Quaternion>>> roleRest =
            new Dictionary<string, Dictionary<string, KeyValuePair<Vector3, Quaternion>>>();

        static List<Transform> HipsChain(G.Character c)
        {
            var res = new List<Transform>();
            Transform hips = null;
            foreach (var t in c.GetComponentsInChildren<Transform>(true))
                if (t != null && string.Equals(t.name, "Hips", StringComparison.OrdinalIgnoreCase)) { hips = t; break; }
            if (hips == null) return res;
            var root = c.transform;
            for (var t = hips.parent; t != null && t.Pointer != root.Pointer; t = t.parent) res.Add(t);
            return res;
        }

        static void RememberRoleRest(G.Character.Characters role, G.Character c)
        {
            string key = role.ToString();
            if (roleRest.ContainsKey(key) || c == null) return;
            var d = new Dictionary<string, KeyValuePair<Vector3, Quaternion>>();
            foreach (var t in HipsChain(c)) if (!d.ContainsKey(t.name)) d[t.name] = new KeyValuePair<Vector3, Quaternion>(t.localPosition, t.localRotation);
            roleRest[key] = d;
        }

        /// <summary>
        /// Transposed girl: the source clips also move the hips and the nodes above them (to kneel, lean...),
        /// but with values made for the original girl's rig. We keep only the *movement* (clip value minus the
        /// original girl's rest value) and apply it on top of the new girl's own rest values.
        /// </summary>
        static void CaptureUpperNodes(Actor a)
        {
            a.UpperNodes.Clear(); a.UpperPos.Clear(); a.UpperRot.Clear(); a.UpperScale.Clear();
            a.SrcPos.Clear(); a.SrcRot.Clear(); a.LastPos.Clear(); a.LastRot.Clear();
            if (!a.ClipMode || a.Ch == null) return;
            try
            {
                if (!roleRest.ContainsKey(a.Role.ToString())) RememberRoleRest(a.Role, FindCharacter(a.Role));
                roleRest.TryGetValue(a.Role.ToString(), out var src);
                foreach (var t in HipsChain(a.Ch))
                {
                    a.UpperNodes.Add(t); a.UpperPos.Add(t.localPosition); a.UpperRot.Add(t.localRotation); a.UpperScale.Add(t.localScale);
                    if (src != null && src.TryGetValue(t.name, out var kv)) { a.SrcPos.Add(kv.Key); a.SrcRot.Add(kv.Value); }
                    else { a.SrcPos.Add(t.localPosition); a.SrcRot.Add(t.localRotation); }
                    a.LastPos.Add(new Vector3(float.NaN, 0, 0)); a.LastRot.Add(Quaternion.identity);
                }
                Log.Msg($"{a.Ch.name}: {a.UpperNodes.Count} node(s) above the hips kept at rest ({string.Join(", ", a.UpperNodes.Select(n => n.name))})");
            }
            catch (Exception e) { Log.Warning("CaptureUpperNodes: " + e.Message); }
        }

        /// <summary>LateUpdate, after the animation: rebase the hips chain on the new girl's rest pose.</summary>
        static void RetargetUpperNodes(Actor a)
        {
            for (int i = 0; i < a.UpperNodes.Count; i++)
            {
                try
                {
                    var t = a.UpperNodes[i];
                    if (t == null) continue;
                    Vector3 cp = t.localPosition; Quaternion cr = t.localRotation;
                    // unchanged since our last write = not animated this frame: no movement
                    bool untouched = !float.IsNaN(a.LastPos[i].x) && (cp - a.LastPos[i]).sqrMagnitude < 1e-10f && Quaternion.Angle(cr, a.LastRot[i]) < 0.01f;
                    if (untouched) { cp = a.SrcPos[i]; cr = a.SrcRot[i]; }
                    Vector3 np = a.UpperPos[i] + (cp - a.SrcPos[i]);
                    Quaternion nr = a.UpperRot[i] * (Quaternion.Inverse(a.SrcRot[i]) * cr);
                    t.localPosition = np; t.localRotation = nr; t.localScale = a.UpperScale[i];
                    a.LastPos[i] = np; a.LastRot[i] = nr;
                }
                catch { }
            }
        }

        static void RestoreUpperNodes(Actor a)
        {
            for (int i = 0; i < a.UpperNodes.Count; i++)
            {
                try
                {
                    var t = a.UpperNodes[i];
                    if (t == null) continue;
                    t.localPosition = a.UpperPos[i]; t.localRotation = a.UpperRot[i]; t.localScale = a.UpperScale[i];
                }
                catch { }
            }
        }

        /// <summary>Plays a state on whichever animator layer contains it.</summary>
        class ExtraRun { public Actor A; public int Layer; public string Name; public float Start; }
        static readonly List<ExtraRun> extras = new List<ExtraRun>();

        static void PlayExtra(Actor a, string name)
        {
            if (string.IsNullOrEmpty(name) || a.Anim == null) return;
            int hash = Animator.StringToHash(name);
            for (int l = 0; l < a.Anim.layerCount; l++)
            {
                try
                {
                    if (!a.Anim.HasState(l, hash)) continue;
                    if (l > 0) a.Anim.SetLayerWeight(l, 1f);
                    a.Anim.CrossFadeInFixedTime(name, 0.2f, l, 0f);
                    if (l > 0) extras.Add(new ExtraRun { A = a, Layer = l, Name = name, Start = Time.time });
                    return;
                }
                catch { }
            }
        }

        /// <summary>
        /// A layered extra (face...) only plays once: when it's over, release its layer like the game does,
        /// otherwise its last frame keeps overriding the head/face of the main animation.
        /// </summary>
        static void TickExtras()
        {
            for (int i = extras.Count - 1; i >= 0; i--)
            {
                var e = extras[i];
                try
                {
                    if (e.A.Anim == null) { extras.RemoveAt(i); continue; }
                    if (Time.time - e.Start < 0.3f) continue;
                    var st = e.A.Anim.GetCurrentAnimatorStateInfo(e.Layer);
                    bool done = !e.A.Anim.IsInTransition(e.Layer) && (!st.IsName(e.Name) || (!st.loop && st.normalizedTime >= 0.99f));
                    if (!done) continue;
                    float w = e.A.Anim.GetLayerWeight(e.Layer) - Time.deltaTime * 4f;      // ~0.25 s fade out
                    e.A.Anim.SetLayerWeight(e.Layer, Mathf.Max(0f, w));
                    if (w <= 0f) extras.RemoveAt(i);
                }
                catch { extras.RemoveAt(i); }
            }
        }

        /// <summary>
        /// Prepares an animator/character for a scene like the game does: override layers left over from the
        /// previous action (arm, upper body, face...) are muted and look-at / IK solvers are switched off,
        /// otherwise they bend the pose or make the head look somewhere else.
        /// </summary>
        static readonly string[] muteLayerWords = { "upperbody", "rightarm", "leftarm", "arm", "override", "speak", "hand", "hold", "carry", "phone", "additional" };
        static readonly string[] ikWords = { "lookat", "aimik", "fullbodybipedik", "grounder", "headrig", "rigbuilder", "multiaim", "headlook", "limbik", "ccdik", "fabrik" };
        static readonly List<Behaviour> disabledIk = new List<Behaviour>();

        static void PrepareForScene(Animator an, GameObject root)
        {
            try
            {
                if (an != null)
                {
                    var muted = new List<string>();
                    for (int l = 1; l < an.layerCount; l++)
                    {
                        string n = (an.GetLayerName(l) ?? "").ToLowerInvariant().Replace(" ", "");
                        if (muteLayerWords.Any(w => n.Contains(w)) && an.GetLayerWeight(l) > 0f) { an.SetLayerWeight(l, 0f); muted.Add(an.GetLayerName(l)); }
                    }
                    if (muted.Count > 0) Log.Msg($"Layers muted on {(root != null ? root.name : "player")}: {string.Join(", ", muted)}");
                }
                if (root != null)
                {
                    foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                    {
                        if (b == null || !b.enabled) continue;
                        string tn = b.GetIl2CppType().Name.ToLowerInvariant();
                        if (ikWords.Any(w => tn.Contains(w))) { b.enabled = false; disabledIk.Add(b); }
                    }
                }
            }
            catch (Exception e) { Log.Warning("PrepareForScene: " + e.Message); }
        }

        static void RestoreIk(GameObject root)
        {
            for (int i = disabledIk.Count - 1; i >= 0; i--)
            {
                var b = disabledIk[i];
                try
                {
                    if (b == null) { disabledIk.RemoveAt(i); continue; }
                    if (root == null || b.transform.IsChildOf(root.transform)) { b.enabled = true; disabledIk.RemoveAt(i); }
                }
                catch { disabledIk.RemoveAt(i); }
            }
        }

        static G.LoveScene placedBy;
        /// <summary>Moves the player and the roles to the positions of the scene this step belongs to.</summary>
        static void ApplyPlacement(G.LoveScene sc)
        {
            if (sc == null || (placedBy != null && placedBy.Pointer == sc.Pointer)) return;
            placedBy = sc;
            try
            {
                var pa = actors.FirstOrDefault(a => a.IsPlayer);
                if (pa != null) { pa.BasePos = sc.playerPos; pa.BaseRot = sc.playerRot; }
                if (sc.characters != null)
                    foreach (var lc in sc.characters)
                    {
                        if (lc == null) continue;
                        var a = ActorFor(lc.character);
                        if (a == null) continue;
                        a.BasePos = lc.initPos; a.BaseRot = lc.initRot;
                    }
                ApplyAdjust();
                Log.Msg($"Duo · placement of scene '{sc.name}'");
            }
            catch (Exception e) { Log.Warning("ApplyPlacement: " + e.Message); }
        }

        // ------------------------------------------------------------------ user placement adjustment
        // Some scenes (developer test scenes) have wrong placements in the game data: the whole scene can be
        // turned / moved from the bar, and the adjustment is saved per scene.
        class Adjust { public float Yaw; public Vector3 Off; }
        static Dictionary<string, Adjust> adjusts;
        static string AdjustFile => System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SandboxHouse", "placements.txt");
        static string AdjustKey => (placedBy ?? scene)?.name ?? "";

        static void LoadAdjusts()
        {
            if (adjusts != null) return;
            adjusts = new Dictionary<string, Adjust>();
            try
            {
                if (!System.IO.File.Exists(AdjustFile)) return;
                foreach (var line in System.IO.File.ReadAllLines(AdjustFile))
                {
                    var p = line.Split(';');
                    if (p.Length < 5) continue;
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    adjusts[p[0]] = new Adjust { Yaw = float.Parse(p[1], ci), Off = new Vector3(float.Parse(p[2], ci), float.Parse(p[3], ci), float.Parse(p[4], ci)) };
                }
            }
            catch (Exception e) { Log.Warning("placements.txt: " + e.Message); }
        }

        static void SaveAdjusts()
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AdjustFile));
                System.IO.File.WriteAllLines(AdjustFile, adjusts.Select(kv =>
                    $"{kv.Key};{kv.Value.Yaw.ToString(ci)};{kv.Value.Off.x.ToString(ci)};{kv.Value.Off.y.ToString(ci)};{kv.Value.Off.z.ToString(ci)}"));
            }
            catch (Exception e) { Log.Warning("placements.txt: " + e.Message); }
        }

        // Adjustment target: 0 = the whole scene, 1 = the player only, 2.. = one role only
        static int adjTarget;
        static string TargetName(int t)
        {
            if (t <= 0) return "All";
            if (t == 1) return "Player";
            var roles = actors.Where(a => !a.IsPlayer).ToList();
            return t - 2 < roles.Count ? roles[t - 2].Role.ToString() : "All";
        }
        static int TargetCount => 2 + actors.Count(a => !a.IsPlayer);

        static Adjust GetAdjust(string key)
        {
            LoadAdjusts();
            if (!adjusts.TryGetValue(key, out var a)) { a = new Adjust(); adjusts[key] = a; }
            return a;
        }
        static Adjust CurAdjust() => GetAdjust(adjTarget <= 0 ? AdjustKey : AdjustKey + "|" + TargetName(adjTarget));
        static Adjust ActorAdjust(Actor a) => GetAdjust(AdjustKey + "|" + (a.IsPlayer ? "Player" : a.Role.ToString()));

        /// <summary>Recomputes everybody's position: scene placement, rotated around the player spot, then moved.</summary>
        static void ApplyAdjust()
        {
            var adj = GetAdjust(AdjustKey);
            var pa = actors.FirstOrDefault(x => x.IsPlayer);
            Vector3 pivot = pa != null ? pa.BasePos : Vector3.zero;
            var q = Quaternion.Euler(0, adj.Yaw, 0);
            foreach (var a in actors)
            {
                a.Pos = pivot + q * (a.BasePos - pivot) + adj.Off;
                a.Rot = q * a.BaseRot;
                // then this character alone: turned on itself and moved
                var own = ActorAdjust(a);
                a.Pos += own.Off;
                a.Rot = Quaternion.Euler(0, own.Yaw, 0) * a.Rot;
                if (!a.IsPlayer && a.Ch != null) { var p = Puppets.Get(a.Ch); if (p != null) { p.Pos = a.Pos; p.Rot = a.Rot; } }
            }
        }

        static void Nudge(float yaw, Vector3 off)
        {
            var adj = CurAdjust();
            adj.Yaw = (adj.Yaw + yaw) % 360f;
            adj.Off += off;
            ApplyAdjust();
            SaveAdjusts();
        }

        static void ResetAdjust()
        {
            var adj = CurAdjust();
            adj.Yaw = 0; adj.Off = Vector3.zero;
            ApplyAdjust(); SaveAdjusts();
        }

        /// <summary>Starts playing a state (or its retargeted clip) from the beginning.</summary>
        static void Begin(Actor a, string name, float fade)
        {
            a.PhaseStart = Time.time;
            if (string.IsNullOrEmpty(name) || a.Anim == null) return;
            if (!a.ClipMode)
            {
                if (!HasState(a.Anim, name)) { Log.Warning($"State not found for {a.Role}: '{name}'"); return; }
                a.Anim.enabled = true;
                if (fade > 0f) a.Anim.CrossFadeInFixedTime(name, fade, 0, 0f);
                else a.Anim.Play(name, 0, 0f);
                return;
            }
            var clip = ResolveClip(a.Role, name);
            if (clip == null) { Log.Warning($"Animation '{name}' not found to retarget onto {a.Ch.name}"); return; }
            a.CurClip = clip;
            var p = Puppets.Get(a.Ch);
            if (p != null) Puppets.Play(p, clip, false);
        }

        /// <summary>Has the actor finished one pass of its current animation?</summary>
        static bool PassDone(Actor a, string name)
        {
            if (a.Anim == null || name == null) return true;
            if (a.ClipMode)
            {
                if (a.CurClip == null) return true;
                return (Time.time - a.PhaseStart) * speed >= a.CurClip.length * 0.98f;
            }
            var st = a.Anim.GetCurrentAnimatorStateInfo(0);
            if (a.Anim.IsInTransition(0))
            {
                var next = a.Anim.GetNextAnimatorStateInfo(0);
                if (next.IsName(name)) return false;          // still fading in
                return st.IsName(name);                       // controller is leaving our state (e.g. back to idle)
            }
            // The game's controller may switch by itself to the idle state after one pass
            // (manual-control steps: one press = one pass). That also means "pass done".
            if (!st.IsName(name)) return Time.time - a.PhaseStart > 0.35f;
            return st.normalizedTime >= 0.98f;
        }

        static void ApplySpeed()
        {
            if (steps.Count == 0) return;
            float stepSpeed = steps[cur].AnimSpeed;
            foreach (var a in actors)
            {
                if (a.Anim == null || a.ClipMode) continue;
                // The loop states are driven by the game's "hSpeed" parameter (its rhythm control).
                if (HasParam(a.Anim, "hSpeed")) { a.Anim.SetFloat("hSpeed", speed); a.Anim.speed = stepSpeed; }
                else a.Anim.speed = speed * stepSpeed;
            }
        }

        public static void LateTick()
        {
            if (!Active) return;
            try
            {
                var pa = actors.FirstOrDefault(a => a.IsPlayer);
                if (pa != null) PlayerRoot().SetPositionAndRotation(pa.Pos, pa.Rot);
                foreach (var a in actors) if (a.ClipMode && a.UpperNodes.Count > 0) RestoreUpperNodes(a);

                // 1) start animation finished -> enter the loop (each actor on its own)
                foreach (var a in actors)
                    if (a.InStart && PassDone(a, a.StartName)) { a.InStart = false; Begin(a, a.LoopName, 0.15f); }

                // 2) automatic repetition: when the reference actor (the player) ends a loop pass,
                //    everybody restarts the loop together, like pressing the action button in the game.
                var reference = actors.FirstOrDefault(a => a.IsPlayer && a.LoopName != null) ?? actors.FirstOrDefault(a => a.LoopName != null);
                if (reference != null && !reference.InStart && actors.All(a => !a.InStart) && PassDone(reference, reference.LoopName))
                {
                    bool looping = false;
                    try
                    {
                        var rs = reference.Anim.GetCurrentAnimatorStateInfo(0);
                        // a looping clip that is still playing our loop state needs no restart
                        looping = !reference.ClipMode && rs.IsName(reference.LoopName) && rs.loop && !reference.Anim.IsInTransition(0);
                    }
                    catch { }
                    if (!looping || actors.Any(a => a.ClipMode))
                        foreach (var a in actors) if (a.LoopName != null) Begin(a, a.LoopName, 0.12f);   // blend into the next pass like the game does
                }

                // 3) each actor on its own: a loop that doesn't repeat by itself (non-looping clip, or the
                //    controller went back to idle) is restarted even if the reference keeps looping.
                foreach (var a in actors)
                {
                    if (a.InStart || a.LoopName == null || a.Anim == null || a == reference) continue;
                    if (Time.time - a.PhaseStart < 0.3f) continue;
                    bool selfLoops = false;
                    try
                    {
                        if (!a.ClipMode)
                        {
                            var st = a.Anim.GetCurrentAnimatorStateInfo(0);
                            selfLoops = st.IsName(a.LoopName) && st.loop && !a.Anim.IsInTransition(0);
                        }
                        else selfLoops = a.CurClip != null && a.CurClip.isLooping;
                    }
                    catch { }
                    if (!selfLoops && PassDone(a, a.LoopName)) Begin(a, a.LoopName, 0.12f);
                }

                TickExtras();
                Sounds();

                if (auto && Time.time - stepStart > loopSeconds && actors.All(a => !a.InStart))
                    PlayStep(cur + 1);
            }
            catch (Exception e) { Log.Warning("DuoScene.LateTick: " + e.Message); }
        }

        static void Sounds()
        {
            G.SexSound type = steps[cur].Sound;
            if (type == G.SexSound.NONE) return;
            foreach (var a in actors)
            {
                if (a.IsPlayer || a.Ch == null) continue;
                // The voice belongs to the role (the scene's original girl), whoever plays it.
                var roleChar = FindCharacter(a.Role) ?? a.Ch;
                var voice = roleChar.GetComponentInChildren<G.CharacterSounds>();
                var body = a.Ch.GetComponentInChildren<G.CharacterSounds>();
                if (voice == null) continue;
                if (Time.time >= nextSound)
                {
                    float len = 1f;
                    try
                    {
                        len = a.ClipMode && a.CurClip != null ? a.CurClip.length / speed
                            : a.Anim.GetCurrentAnimatorStateInfo(0).length / Mathf.Max(0.1f, a.Anim.speed);
                    }
                    catch { }
                    nextSound = Time.time + Mathf.Clamp(len, 0.35f, 2.5f);
                    bool mouth = type == G.SexSound.KISSING || type == G.SexSound.BJ;
                    PlayFrom(a, body, voice, type == G.SexSound.KISSING ? voice.kissingSounds
                           : type == G.SexSound.BJ ? voice.blowjobSoundsMedium
                           : type == G.SexSound.SEX ? voice.sexSounds
                           : voice.sexBreathingSounds, mouth);
                }
                if (Time.time >= nextMoan && type != G.SexSound.KISSING && type != G.SexSound.BJ)
                {
                    nextMoan = Time.time + 2.5f + (float)rng.NextDouble() * 3f;
                    PlayFrom(a, body, voice, speed > 1.3f ? voice.moanLoudSounds : voice.moanNormalSounds, true);
                }
            }
        }

        /// <summary>Plays a random clip of the role's voice from the body that is actually in the scene.</summary>
        static void PlayFrom(Actor a, G.CharacterSounds body, G.CharacterSounds voice,
                             Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<AudioClip> clips, bool mouth)
        {
            try
            {
                if (clips == null || clips.Length == 0) return;
                if (!a.ClipMode || body == null) { voice.PlayRandSound(clips); return; }   // original girl: game's own logic
                var clip = clips[rng.Next(clips.Length)];
                if (clip == null) return;
                AudioSource src = null;
                try { src = mouth ? body.sexMouthSoundSource : body.pussySoundSource; } catch { }
                if (src == null) try { src = body.sexMouthSoundSource; } catch { }
                if (src != null) src.PlayOneShot(clip);
                else AudioSource.PlayClipAtPoint(clip, a.Ch.transform.position + Vector3.up * 1.2f);
            }
            catch { }
        }

        // ------------------------------------------------------------------ stop
        /// <summary>Gives a character back its pre-scene place and state.</summary>
        static void RestoreActor(Actor a)
        {
            if (a.Ch == null) return;
            try
            {
                var p = Puppets.Get(a.Ch);
                if (p != null) { Puppets.StopClip(p); p.NoGround = false; }
                RestoreUpperNodes(a);
                RestoreIk(a.Ch.gameObject);
                if (a.Anim != null) { a.Anim.speed = 1f; a.Anim.Rebind(); }
                if (a.WasPuppet)
                {
                    p = Puppets.Take(a.Ch, a.SavedPos, a.SavedRot, false);
                    if (a.SavedClip != null) Puppets.Play(p, a.SavedClip, SandboxMod.LoopAnims); else Puppets.PlayIdle(p);
                }
                else if (p != null)
                {
                    p.Pos = a.SavedPos; p.Rot = a.SavedRot;
                    a.Ch.transform.SetPositionAndRotation(a.SavedPos, a.SavedRot);
                    Puppets.Release(p);
                }
            }
            catch (Exception e) { Log.Warning("DuoScene.Restore: " + e.Message); }
            try { Clothes.Reapply(a.Ch); } catch { }
        }

        public static void Stop()
        {
            if (scene == null && actors.Count == 0) return;
            Log.Msg("Two-person scene stopped, back to normal.");
            var pm = G.PlayerManager.instance;
            foreach (var a in actors)
            {
                if (!a.IsPlayer) { RestoreActor(a); continue; }
                if (pm == null) continue;
                try
                {
                    if (a.Anim != null) { a.Anim.speed = 1f; a.Anim.Rebind(); }
                    RestoreIk(pm.gameObject);
                    PlayerRoot().SetPositionAndRotation(plSavedPos, plSavedRot);
                    if (pm.playerController != null) pm.playerController.enabled = true;
                    try { pm.EnableIK(true); } catch { }
                    try { pm.SetPenisEnabled(false); } catch { }
                    try { pm.allowMovement(true); } catch { }
                }
                catch (Exception e) { Log.Warning("DuoScene.Stop player: " + e.Message); }
            }
            RestoreIk(null);
            actors.Clear(); steps.Clear(); scene = null; extras.Clear(); roleRest.Clear();
            SandboxMod.I.BlockPlayer(false);
        }

        /// <summary>Scene unloaded: drop state without touching destroyed objects.</summary>
        public static void Forget() { actors.Clear(); steps.Clear(); scene = null; clipCache.Clear(); disabledIk.Clear(); paramCache.Clear(); roleRest.Clear(); extras.Clear(); }

        // ------------------------------------------------------------------ controls
        public static void Tick()
        {
            if (!Active) return;
            if (Input.GetKeyDown(KeyCode.Space)) MouseFree = !MouseFree;
            if (!FreeCam.Active)
            {
                if (MouseFree) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                else { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { Stop(); return; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) PlayStep(cur + 1);
            if (Input.GetKeyDown(KeyCode.LeftArrow)) PlayStep(cur - 1);
            if (Input.GetKeyDown(KeyCode.UpArrow)) { speed = Mathf.Min(3f, speed + 0.1f); ApplySpeed(); }
            if (Input.GetKeyDown(KeyCode.DownArrow)) { speed = Mathf.Max(0.2f, speed - 0.1f); ApplySpeed(); }
        }

        public static void Draw()
        {
            if (!Active) return;
            try
            {
                var roles = actors.Where(a => !a.IsPlayer).ToList();
                float w = 660, h = 246 + roles.Count * 32;
                var r = new Rect((Screen.width - w) / 2f, Screen.height - h - 24, w, h);
                GUI.Box(r, ""); GUI.Box(r, "");
                GUI.Label(new Rect(r.x + 10, r.y + 6, w - 20, 22), $"{LoveScenes.Title(scene)}   —   {info}");
                float y = r.y + 34, x = r.x + 10;
                if (GUI.Button(new Rect(x, y, 90, 28), "◀ Step")) PlayStep(cur - 1);
                if (GUI.Button(new Rect(x + 96, y, 90, 28), "Step ▶")) PlayStep(cur + 1);
                auto = GUI.Toggle(new Rect(x + 194, y + 5, 70, 22), auto, " Auto");
                if (GUI.Button(new Rect(x + 268, y, 34, 28), "−")) { speed = Mathf.Max(0.2f, speed - 0.1f); ApplySpeed(); }
                GUI.Label(new Rect(x + 306, y + 5, 78, 22), $"speed {speed:0.0}");
                if (GUI.Button(new Rect(x + 386, y, 34, 28), "+")) { speed = Mathf.Min(3f, speed + 0.1f); ApplySpeed(); }
                if (GUI.Button(new Rect(r.xMax - 120, y, 110, 28), "■ Stop")) { Stop(); return; }
                y += 34;

                foreach (var a in roles)
                {
                    GUI.Label(new Rect(x, y + 4, 150, 22), $"Role {a.Role}:");
                    if (GUI.Button(new Rect(x + 150, y, 34, 26), "◀")) { SwapRole(a, -1); return; }
                    GUI.Label(new Rect(x + 190, y + 4, 170, 22), a.Ch != null ? a.Ch.name + (a.ClipMode ? "  (retargeted)" : "") : "?");
                    if (GUI.Button(new Rect(x + 360, y, 34, 26), "▶")) { SwapRole(a, 1); return; }
                    y += 32;
                }

                {
                    var adj = CurAdjust();
                    GUI.Label(new Rect(x, y + 4, 60, 22), "Target:");
                    if (GUI.Button(new Rect(x + 60, y, 30, 26), "◀")) adjTarget = (adjTarget - 1 + TargetCount) % TargetCount;
                    GUI.Label(new Rect(x + 96, y + 4, 90, 22), TargetName(adjTarget));
                    if (GUI.Button(new Rect(x + 186, y, 30, 26), "▶")) adjTarget = (adjTarget + 1) % TargetCount;
                    GUI.Label(new Rect(x + 226, y + 4, w - 240, 22), adjTarget == 0 ? "(moves/turns the whole scene)" : "(moves/turns only this character, on itself)");
                    y += 30;
                    GUI.Label(new Rect(x, y + 4, 90, 22), $"Placement:");
                    float bx = x + 90; float bw2 = 44;
                    if (GUI.Button(new Rect(bx, y, bw2, 26), "⟲90")) Nudge(-90, Vector3.zero); bx += bw2 + 2;
                    if (GUI.Button(new Rect(bx, y, bw2, 26), "⟳90")) Nudge(90, Vector3.zero); bx += bw2 + 2;
                    if (GUI.Button(new Rect(bx, y, bw2, 26), "⟲10")) Nudge(-10, Vector3.zero); bx += bw2 + 2;
                    if (GUI.Button(new Rect(bx, y, bw2, 26), "⟳10")) Nudge(10, Vector3.zero); bx += bw2 + 6;
                    if (GUI.Button(new Rect(bx, y, 30, 26), "X-")) Nudge(0, new Vector3(-0.05f, 0, 0)); bx += 32;
                    if (GUI.Button(new Rect(bx, y, 30, 26), "X+")) Nudge(0, new Vector3(0.05f, 0, 0)); bx += 34;
                    if (GUI.Button(new Rect(bx, y, 30, 26), "Z-")) Nudge(0, new Vector3(0, 0, -0.05f)); bx += 32;
                    if (GUI.Button(new Rect(bx, y, 30, 26), "Z+")) Nudge(0, new Vector3(0, 0, 0.05f)); bx += 34;
                    if (GUI.Button(new Rect(bx, y, 30, 26), "▼")) Nudge(0, new Vector3(0, -0.03f, 0)); bx += 32;
                    if (GUI.Button(new Rect(bx, y, 30, 26), "▲")) Nudge(0, new Vector3(0, 0.03f, 0)); bx += 34;
                    if (GUI.Button(new Rect(bx, y, 40, 26), "0")) ResetAdjust();
                    y += 30;
                    GUI.Label(new Rect(x, y, w - 20, 22), $"   rotation {adj.Yaw:0}°  offset {adj.Off.x:0.00} / {adj.Off.y:0.00} / {adj.Off.z:0.00} m  (saved for « {AdjustKey} » / {TargetName(adjTarget)})");
                    y += 26;
                }
                GUI.Label(new Rect(x, y + 4, 70, 22), "Rhythm:");
                for (int k = 1; k <= 3; k++)
                    if (GUI.Button(new Rect(x + 70 + (k - 1) * 40, y, 36, 26), (rhythm == k ? "▶" : "") + k)) SetRhythm(k);
                y += 32;
                GUI.Label(new Rect(x, y, 200, 22), $"Time per step (auto): {loopSeconds:0}s");
                loopSeconds = GUI.HorizontalSlider(new Rect(x + 200, y + 6, 160, 16), loopSeconds, 4f, 40f);
                GUI.Label(new Rect(x + 370, y, w - 380, 22), "←/→ steps · ↑/↓ speed · Esc stop");
                y += 24;
                var old = GUI.color;
                GUI.color = MouseFree ? Color.green : Color.yellow;
                GUI.Label(new Rect(x, y, w - 20, 22), MouseFree ? "SPACE: hide the mouse" : ">>> SPACE to show the mouse <<<");
                GUI.color = old;
            }
            catch (Exception e) { Log.Warning("DuoScene.Draw: " + e.Message); }
        }
    }
}
