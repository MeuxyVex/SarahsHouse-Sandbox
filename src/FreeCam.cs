using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using G = Il2Cpp;

namespace SandboxHouse
{
    /// <summary>
    /// Free camera, usable anywhere (sandbox, two-person scenes, normal game).
    /// A clone of the active game camera (same render settings / post effects) is flown around while the
    /// original camera is paused. ZQSD/WASD move, Space/E up, Ctrl/A down, Shift fast, mouse look, wheel speed.
    /// </summary>
    internal static class FreeCam
    {
        static MelonLogger.Instance Log => SandboxMod.I.LoggerInstance;

        public static bool Active => cam != null;
        public static Camera CurrentCamera => cam;
        static Camera cam;
        static Camera original;
        static float yaw, pitch;
        static float moveSpeed = 3f;
        static Vector3 lastMouse;
        static bool blockedPlayer;

        static Camera FindGameCamera()
        {
            // 1) the player's own view cameras (first person / love-scene camera)
            try
            {
                var pm = G.PlayerManager.instance;
                if (pm != null)
                {
                    foreach (var c in new[] { pm.currentSelectedCamera, pm.firstPersonHCamera, pm.mainCamera })
                        if (c != null && c.enabled && c.gameObject.activeInHierarchy && c.targetTexture == null) return c;
                }
            }
            catch { }
            // 2) the camera tagged MainCamera
            try { var m = Camera.main; if (m != null && m.enabled) return m; } catch { }
            // 3) any enabled full-screen camera, lowest depth = the base camera (higher ones are UI overlays)
            Camera best = null;
            try
            {
                foreach (var c in Camera.allCameras)
                {
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || c.targetTexture != null) continue;
                    if (best == null || c.depth < best.depth) best = c;
                }
            }
            catch { }
            return best;
        }

        public static void Toggle()
        {
            if (Active) Exit(); else Enter();
        }

        static void Enter()
        {
            try
            {
                original = FindGameCamera();
                if (original == null) { Log.Warning("FreeCam: no camera found."); return; }
                try
                {
                    var list = new System.Text.StringBuilder();
                    foreach (var c in Camera.allCameras) if (c != null) list.Append($"{c.name}(depth {c.depth}, {(c.enabled ? "on" : "off")}) ");
                    Log.Msg($"FreeCam: copied camera = {original.name} (tag {original.tag}, depth {original.depth}). Active cameras: {list}");
                }
                catch { }

                // Like UnityExplorer's free cam: a second, independent camera drawn on top of the player's one.
                // The player's camera keeps running untouched, so the game's first-person tricks
                // (hiding the head for that camera) don't apply to ours.
                var go = new GameObject("SandboxFreeCam");
                cam = go.AddComponent<Camera>();
                cam.fieldOfView = original.fieldOfView;
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = Mathf.Max(200f, original.farClipPlane);
                cam.clearFlags = original.clearFlags;
                cam.backgroundColor = original.backgroundColor;
                cam.cullingMask = original.cullingMask | PlayerLayers();
                cam.depth = original.depth + 1f;          // above the player camera, below the UI camera
                cam.allowHDR = original.allowHDR;
                go.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
                var e = original.transform.eulerAngles;
                yaw = e.y; pitch = e.x > 180f ? e.x - 360f : e.x;
                cam.enabled = true;
                // Pause the player's camera, like UnityExplorer does: the game hides the player's head while
                // that camera renders (until the end of the frame), so it must not render at all.
                original.enabled = false;
                CollectHead();
                try { G.PlayerManager.instance?.CloseEyesScreen(false); } catch { }

                var pm = G.PlayerManager.instance;
                blockedPlayer = false;
                if (pm != null)
                {
                    try { pm.setInputAllowed(false); pm.SetCameraRotationBlocked(true); blockedPlayer = true; } catch { }
                }
                lastMouse = Input.mousePosition;
                Log.Msg("FreeCam on.");
            }
            catch (Exception e) { Log.Error("FreeCam.Enter: " + e); Exit(); }
        }

        public static void Exit()
        {
            RestoreHead();
            try { if (cam != null) UnityEngine.Object.Destroy(cam.gameObject); } catch { }
            cam = null;
            try { if (original != null) original.enabled = true; } catch { }
            original = null;
            if (blockedPlayer && !DuoScene.Active && !Interaction.IsOpen)
            {
                SandboxMod.I.BlockPlayer(false);
            }
            blockedPlayer = false;
            Log.Msg("FreeCam off.");
        }

        /// <summary>Scene unloaded: the clone is gone with it.</summary>
        public static void Forget() { cam = null; original = null; blockedPlayer = false; savedScales.Clear(); savedShadows.Clear(); enabledRenderers.Clear(); activated.Clear(); headParts.Clear(); }

        /// <summary>The player's model ("Hero", where its Animator lives) and the PlayerManager object.</summary>
        static List<GameObject> PlayerRoots()
        {
            var res = new List<GameObject>();
            var pm = G.PlayerManager.instance;
            if (pm == null) return res;
            try { var an = pm.GetAnimator(); if (an != null) res.Add(an.gameObject); } catch { }
            try { if (pm.playerModel != null) res.Add(pm.playerModel); } catch { }
            try { if (pm.playerObject != null) res.Add(pm.playerObject); } catch { }
            res.Add(pm.gameObject);
            return res.GroupBy(g => g.Pointer).Select(g => g.First()).ToList();
        }

        static IEnumerable<T> InPlayer<T>() where T : Component
        {
            var seen = new HashSet<IntPtr>();
            foreach (var root in PlayerRoots())
                foreach (var c in root.GetComponentsInChildren<T>(true))
                    if (c != null && seen.Add(c.Pointer)) yield return c;
        }

        /// <summary>Layers of the player's renderers (in case the player camera culls them).</summary>
        static int PlayerLayers()
        {
            int mask = 0;
            try
            {
                var pm = G.PlayerManager.instance;
                foreach (var r in InPlayer<Renderer>()) mask |= 1 << r.gameObject.layer;
            }
            catch { }
            return mask;
        }

        // ------------------------------------------------------------------ player head
        // First person: the game hides the "Head"/"Hair" objects of the Hero model or shrinks the head bones.
        // While flying we show them (only our camera matters then) and put everything back on exit.
        static readonly string[] headNames = { "Head", "Hair", "Head_s", "Head_bone", "HeadRef" };
        static readonly List<KeyValuePair<Transform, Vector3>> savedScales = new List<KeyValuePair<Transform, Vector3>>();
        static readonly List<GameObject> activated = new List<GameObject>();
        static readonly List<Renderer> enabledRenderers = new List<Renderer>();
        static readonly List<KeyValuePair<Renderer, UnityEngine.Rendering.ShadowCastingMode>> savedShadows = new List<KeyValuePair<Renderer, UnityEngine.Rendering.ShadowCastingMode>>();
        static readonly List<Transform> headParts = new List<Transform>();
        static readonly List<Renderer> forcedOff = new List<Renderer>();
        static bool headLogged;

        static void CollectHead()
        {
            headParts.Clear();
            var pm = G.PlayerManager.instance;
            if (pm == null) return;
            foreach (var t in InPlayer<Transform>())
                if (t != null && headNames.Any(n => string.Equals(t.name, n, StringComparison.OrdinalIgnoreCase))) headParts.Add(t);
            headLogged = false;
            // diagnostic: how is the head hidden?
            try
            {
                var sb = new System.Text.StringBuilder($"FreeCam head diagnostic (roots: {string.Join(", ", PlayerRoots().Select(g => g.name))}) :");
                foreach (var r in InPlayer<Renderer>())
                {
                    if (r == null) continue;
                    string n = r.name.ToLowerInvariant();
                    if (!(n.Contains("head") || n.Contains("hair") || n.Contains("body"))) continue;
                    string mats = "";
                    try { foreach (var m in r.sharedMaterials) if (m != null) mats += m.name + "/" + (m.shader != null ? m.shader.name : "?") + " "; } catch { }
                    sb.Append($" | {r.name}: actif={r.gameObject.activeInHierarchy} enabled={r.enabled} forceOff={r.forceRenderingOff} ombre={r.shadowCastingMode} calque={r.gameObject.layer} visible={r.isVisible} renderingLayerMask={r.renderingLayerMask} mats={mats}");
                    var smr = r.TryCast<SkinnedMeshRenderer>();
                    if (smr != null && smr.rootBone != null) sb.Append($" rootBone={smr.rootBone.name} scale={smr.rootBone.lossyScale}");
                }
                foreach (var t in InPlayer<Transform>())
                {
                    if (t == null) continue;
                    string n = t.name.ToLowerInvariant();
                    if ((n == "neck" || n.StartsWith("head")) && t.lossyScale.sqrMagnitude < 0.5f) sb.Append($" | bone {t.name} shrunk lossyScale={t.lossyScale}");
                }
                Log.Msg(sb.ToString());
            }
            catch (Exception e) { Log.Warning("head diag: " + e.Message); }
        }

        static float nextEyes;

        public static void LateTick()
        {
            if (!Active) return;
            try { if (original != null && original.enabled) original.enabled = false; } catch { }
            if (Time.unscaledTime >= nextEyes)
            {
                nextEyes = Time.unscaledTime + 0.15f;
                try { var pmE = G.PlayerManager.instance; if (pmE != null) pmE.CloseEyesScreen(false); } catch { }
            }
            var report = new System.Text.StringBuilder();
            foreach (var t in headParts)
            {
                try
                {
                    if (t == null) continue;
                    if (!t.gameObject.activeSelf) { if (!activated.Any(g => g.Pointer == t.gameObject.Pointer)) activated.Add(t.gameObject); t.gameObject.SetActive(true); report.Append($"{t.name}:enabled "); }
                    if (t.localScale.sqrMagnitude < 0.5f) { if (!savedScales.Any(k => k.Key.Pointer == t.Pointer)) savedScales.Add(new KeyValuePair<Transform, Vector3>(t, t.localScale)); t.localScale = Vector3.one; report.Append($"{t.name}:taille "); }
                    var r = t.GetComponent<Renderer>();
                    if (r != null)
                    {
                        if (r.forceRenderingOff) { r.forceRenderingOff = false; if (!forcedOff.Any(x => x.Pointer == r.Pointer)) forcedOff.Add(r); report.Append($"{t.name}:forceRenderingOff "); }
                        if (!r.enabled) { r.enabled = true; if (!enabledRenderers.Any(x => x.Pointer == r.Pointer)) enabledRenderers.Add(r); report.Append($"{t.name}:rendu "); }
                        if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                        { if (!savedShadows.Any(k => k.Key.Pointer == r.Pointer)) savedShadows.Add(new KeyValuePair<Renderer, UnityEngine.Rendering.ShadowCastingMode>(r, r.shadowCastingMode)); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; report.Append($"{t.name}:ombre "); }
                    }
                }
                catch { }
            }
            if (!headLogged && report.Length > 0) { headLogged = true; Log.Msg("FreeCam player head restored: " + report); }
        }

        static void RestoreHead()
        {
            // only the first saved value of each object is the original one
            var done = new HashSet<IntPtr>();
            foreach (var kv in savedScales) { try { if (kv.Key != null && done.Add(kv.Key.Pointer)) kv.Key.localScale = kv.Value; } catch { } }
            done.Clear();
            foreach (var kv in savedShadows) { try { if (kv.Key != null && done.Add(kv.Key.Pointer)) kv.Key.shadowCastingMode = kv.Value; } catch { } }
            foreach (var r in enabledRenderers) { try { if (r != null) r.enabled = false; } catch { } }
            foreach (var r in forcedOff) { try { if (r != null) r.forceRenderingOff = true; } catch { } }
            forcedOff.Clear();
            foreach (var g in activated) { try { if (g != null) g.SetActive(false); } catch { } }
            savedScales.Clear(); savedShadows.Clear(); enabledRenderers.Clear(); activated.Clear(); headParts.Clear();
        }

        public static void Tick(bool uiOpen)
        {
            if (!Active) return;
            if (cam == null) { Forget(); return; }
            try
            {
                float dt = Time.unscaledDeltaTime;

                // Mouse look: always, unless one of our menus is open (then hold right click).
                bool look = !uiOpen || Input.GetMouseButton(1);
                if (look)
                {
                    Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
                    float mx = 0, my = 0;
                    try { mx = Input.GetAxisRaw("Mouse X"); my = Input.GetAxisRaw("Mouse Y"); }
                    catch { var d = Input.mousePosition - lastMouse; mx = d.x * 0.1f; my = d.y * 0.1f; }
                    yaw += mx * 2.2f;
                    pitch = Mathf.Clamp(pitch - my * 2.2f, -89f, 89f);
                }
                else { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                lastMouse = Input.mousePosition;
                cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

                float scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.01f) moveSpeed = Mathf.Clamp(moveSpeed * (scroll > 0 ? 1.2f : 1f / 1.2f), 0.2f, 40f);

                Vector3 dir = Vector3.zero;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.Z) || Input.GetKey(KeyCode.UpArrow) && !DuoScene.Active) dir += Vector3.forward;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) && !DuoScene.Active) dir += Vector3.back;
                if (Input.GetKey(KeyCode.D)) dir += Vector3.right;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.Q)) dir += Vector3.left;
                if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.E)) dir += Vector3.up;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C)) dir += Vector3.down;
                float sp = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? 3f : 1f);
                var t = cam.transform;
                Vector3 move = t.forward * dir.z + t.right * dir.x + Vector3.up * dir.y;
                t.position += move * sp * dt;

            }
            catch (Exception e) { Log.Warning("FreeCam.Tick: " + e.Message); }
        }

        public static void Draw()
        {
            if (!Active) return;
            GUI.Label(new Rect(16, Screen.height - 30, 760, 24),
                $"FreeCam ({SandboxMod.FreeCamKey.Value} to quit) · ZQSD/WASD move · Space/E up · Ctrl/C down · Shift fast · wheel speed ({moveSpeed:0.0})");
        }
    }
}
