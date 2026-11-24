using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Rewired;
using UnityEngine;
using TMPro;

[BepInPlugin("com.noms.splitthrottle", "SplitThrottle", "1.3.0")]
public class Plugin : BaseUnityPlugin
{
    // === Rewired Action Names ===
    internal const string ACT_LEFT_UP = "SplitThrottle::LeftUp";
    internal const string ACT_LEFT_DOWN = "SplitThrottle::LeftDown";
    internal const string ACT_RIGHT_UP = "SplitThrottle::RightUp";
    internal const string ACT_RIGHT_DOWN = "SplitThrottle::RightDown";
    internal const string ACT_SYNC = "SplitThrottle::Sync";
    internal const string ACT_LEFT_AXIS = "SplitThrottle::LeftAxis";
    internal const string ACT_RIGHT_AXIS = "SplitThrottle::RightAxis";

    // === Shared State ===
    internal static bool splitActive;
    internal static float leftThrottle;
    internal static float rightThrottle;
    internal static ManualLogSource Log;

    // === Config ===
    internal static ConfigEntry<bool> cfgEnabled;
    internal static ConfigEntry<float> cfgKeyboardStep;
    internal static ConfigEntry<bool> cfgShowOverlay;
    internal static ConfigEntry<int> cfgLeftAxisIndex;
    internal static ConfigEntry<int> cfgRightAxisIndex;
    internal static ConfigEntry<bool> cfgInvertLeft;
    internal static ConfigEntry<bool> cfgInvertRight;
    internal static ConfigEntry<string> cfgControllerName;
    internal static ConfigEntry<string> cfgDisabledAircraft;
    internal static ConfigEntry<bool> cfgHudReadout;
    internal static ConfigEntry<float> cfgHudOffsetX;
    internal static ConfigEntry<float> cfgHudOffsetY;
    internal static ConfigEntry<bool> cfgDualGauges;
    internal static ConfigEntry<string> cfgDualLayout;
    internal static ConfigEntry<float> cfgOuterScale;
    internal static ConfigEntry<KeyboardShortcut> keyLeftUp, keyLeftDown, keyRightUp, keyRightDown, keySync;

    internal static HashSet<string> disabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Engine side cache
    internal static readonly Dictionary<Turbojet, EngineSide> turbojetSideCache = new Dictionary<Turbojet, EngineSide>();
    internal static readonly Dictionary<DuctedFan, EngineSide> ductedFanSideCache = new Dictionary<DuctedFan, EngineSide>();

    internal enum EngineSide { Left, Right, Center }

    // Which engine sides the local aircraft actually has (filled in by the engine patches).
    internal static Aircraft sideAircraft;
    internal static bool hasLeftEngine, hasRightEngine;

    internal static void MarkSide(Aircraft ac, EngineSide side)
    {
        if (ac != sideAircraft) { sideAircraft = ac; hasLeftEngine = hasRightEngine = false; }
        if (side == EngineSide.Left) hasLeftEngine = true;
        else if (side == EngineSide.Right) hasRightEngine = true;
    }

    internal static bool LocalIsTwin(Aircraft ac) => ac != null && ac == sideAircraft && hasLeftEngine && hasRightEngine;

    void Awake()
    {
        Log = Logger;

        cfgEnabled = Config.Bind("General", "Enabled", true, "Enable split throttle mod");
        cfgKeyboardStep = Config.Bind("General", "ThrottleStep", 0.02f,
            "Throttle change per frame when holding keyboard key. ConfigurationManager " +
            "UI clamps to 0.01 minimum — for slower ramps, edit this .cfg in Notepad " +
            "directly (e.g. 0.005). Range is internally unrestricted.");
        cfgShowOverlay = Config.Bind("General", "ShowOverlay", false, "Show the old standalone (grey box) throttle overlay on screen");
        cfgHudReadout = Config.Bind("HUD", "ShowSplitReadout", true,
            "Show 'L xx%  R xx%' under the game's own HUD throttle gauge while split throttle is active");
        cfgHudOffsetX = Config.Bind("HUD", "ReadoutOffsetX", 0f, "Horizontal nudge for the L/R readout (HUD pixels, + = right)");
        cfgHudOffsetY = Config.Bind("HUD", "ReadoutOffsetY", 0f, "Vertical nudge for the L/R readout (HUD pixels, + = up)");
        cfgDualGauges = Config.Bind("HUD", "DualGauges", true,
            "On twin-engine aircraft, show two separate HUD throttle gauges (one per engine) instead of one");
        cfgDualLayout = Config.Bind("HUD", "DualGaugeLayout", "Outer",
            new ConfigDescription("Mirror = left-engine gauge mirrored onto the left side of the HUD. " +
                "Outer = right-engine gauge drawn as a second arc just outside the normal one.",
                new AcceptableValueList<string>("Mirror", "Outer")));
        cfgOuterScale = Config.Bind("HUD", "OuterGaugeScale", 1.25f,
            new ConfigDescription("Outer layout: size of the right-engine arc relative to the normal gauge. " +
                "Raise it if the inner gauge's pointer line overlaps the outer arc. Changes apply live.",
                new AcceptableValueRange<float>(1.05f, 1.8f)));
        cfgDisabledAircraft = Config.Bind("General", "DisabledAircraft", "Chicane,SAH,AttackHelo,Tarantula,Ibis",
            "Comma-separated aircraft names where split throttle is disabled (partial match, case-insensitive)");

        cfgLeftAxisIndex = Config.Bind("HOTAS", "LeftAxisIndex", 1,
            "Rewired joystick axis index for left throttle lever. 0-based.");
        cfgRightAxisIndex = Config.Bind("HOTAS", "RightAxisIndex", 0,
            "Rewired joystick axis index for right throttle lever. 0-based.");
        cfgInvertLeft = Config.Bind("HOTAS", "InvertLeft", false, "Invert left axis");
        cfgInvertRight = Config.Bind("HOTAS", "InvertRight", false, "Invert right axis");
        cfgControllerName = Config.Bind("HOTAS", "ControllerName", "THROTTLE",
            "Partial name match for throttle controller (case-insensitive)");

        ParseBlacklist();
        cfgDisabledAircraft.SettingChanged += (_, __) => ParseBlacklist();

        // Keybinds (replaces Extra Input Framework / Rewired actions)
        keyLeftUp = Config.Bind("Keybinds", "LeftUp", new KeyboardShortcut(KeyCode.None), "Hold to increase left engine throttle");
        keyLeftDown = Config.Bind("Keybinds", "LeftDown", new KeyboardShortcut(KeyCode.None), "Hold to decrease left engine throttle");
        keyRightUp = Config.Bind("Keybinds", "RightUp", new KeyboardShortcut(KeyCode.None), "Hold to increase right engine throttle");
        keyRightDown = Config.Bind("Keybinds", "RightDown", new KeyboardShortcut(KeyCode.None), "Hold to decrease right engine throttle");
        keySync = Config.Bind("Keybinds", "Sync", new KeyboardShortcut(KeyCode.None), "Press to sync both engines to the current main throttle");

        try
        {
            var harmony = new Harmony("com.noms.splitthrottle");
            harmony.PatchAll();
            Logger.LogInfo($"Harmony patched {harmony.GetPatchedMethods().Count()} methods.");
        }
        catch (Exception e)
        {
            Logger.LogError($"Harmony patch FAILED: {e}");
        }

        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
        {
            if (SplitThrottleRunner.Instance != null) return;
            var go = new GameObject("SplitThrottle_Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<SplitThrottleRunner>();
        };

        Logger.LogInfo("SplitThrottle loaded.");
    }

    static void ParseBlacklist()
    {
        disabledNames.Clear();
        foreach (var name in cfgDisabledAircraft.Value.Split(','))
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0) disabledNames.Add(trimmed);
        }
    }

    internal static bool IsDisabledAircraft(Aircraft aircraft)
    {
        if (aircraft == null) return true;
        string acName = ((Component)aircraft).name;
        foreach (var disabled in disabledNames)
        {
            if (acName.IndexOf(disabled, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    internal static Aircraft GetLocalAircraft()
    {
        try
        {
            if (GameManager.GetLocalAircraft(out var ac)) return ac;
            return null;
        }
        catch { return null; }
    }

    // === Harmony Patches ===

    // Append mod marker to Application.version so modded clients only match with each other.
    // This prevents the mod from giving an unfair advantage in lobbies where the host doesn't have it.
    [HarmonyPatch(typeof(Application), "get_version")]
    static class ApplicationVersionPatch
    {
        const string MOD_MARKER = "+SplitThrottle";

        static void Postfix(ref string __result)
        {
            if (__result != null && !__result.Contains(MOD_MARKER))
                __result = __result + MOD_MARKER;
        }
    }

    [HarmonyPatch(typeof(Turbojet), "FixedUpdate")]
    static class TurbojetPatch
    {
        static readonly FieldInfo f_controlInputs = AccessTools.Field(typeof(Turbojet), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(Turbojet), "aircraft");
        [ThreadStatic] static float savedThrottle;

        static void Prefix(Turbojet __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            savedThrottle = inputs.throttle;
            if (!turbojetSideCache.TryGetValue(__instance, out var side))
            {
                Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(__instance.transform.position);
                side = Mathf.Abs(localPos.x) < 0.5f ? EngineSide.Center
                     : localPos.x < 0 ? EngineSide.Left : EngineSide.Right;
                turbojetSideCache[__instance] = side;
            }
            MarkSide(aircraft, side);
            inputs.throttle = side switch
            {
                EngineSide.Left => leftThrottle,
                EngineSide.Right => rightThrottle,
                _ => (leftThrottle + rightThrottle) * 0.5f
            };
        }

        static void Postfix(Turbojet __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    [HarmonyPatch(typeof(DuctedFan), "FixedUpdate")]
    static class DuctedFanPatch
    {
        static readonly FieldInfo f_controlInputs = AccessTools.Field(typeof(DuctedFan), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(DuctedFan), "aircraft");
        [ThreadStatic] static float savedThrottle;

        static void Prefix(DuctedFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            savedThrottle = inputs.throttle;
            if (!ductedFanSideCache.TryGetValue(__instance, out var side))
            {
                Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(((Component)__instance).transform.position);
                side = Mathf.Abs(localPos.x) < 0.5f ? EngineSide.Center
                     : localPos.x < 0 ? EngineSide.Left : EngineSide.Right;
                ductedFanSideCache[__instance] = side;
            }
            MarkSide(aircraft, side);
            if (side != EngineSide.Center)
                inputs.throttle = side == EngineSide.Left ? leftThrottle : rightThrottle;
        }

        static void Postfix(DuctedFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    [HarmonyPatch(typeof(ConstantSpeedProp), "FixedUpdate")]
    static class ConstantSpeedPropPatch
    {
        static readonly FieldInfo f_controlInputs = AccessTools.Field(typeof(ConstantSpeedProp), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(ConstantSpeedProp), "aircraft");

        static void Prefix(ConstantSpeedProp __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(__instance.transform.position);
            MarkSide(aircraft, localPos.x < -0.5f ? EngineSide.Left : localPos.x > 0.5f ? EngineSide.Right : EngineSide.Center);
            if (localPos.x < -0.5f) inputs.throttle = leftThrottle;
            else if (localPos.x > 0.5f) inputs.throttle = rightThrottle;
            else inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }

        static void Postfix(ConstantSpeedProp __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    [HarmonyPatch(typeof(PropFan), "FixedUpdate")]
    static class PropFanPatch
    {
        static readonly FieldInfo f_inputs = AccessTools.Field(typeof(PropFan), "inputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(PropFan), "aircraft");

        static void Prefix(PropFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_inputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(((Component)__instance).transform.position);
            MarkSide(aircraft, localPos.x < -0.5f ? EngineSide.Left : localPos.x > 0.5f ? EngineSide.Right : EngineSide.Center);
            if (localPos.x < -0.5f) inputs.throttle = leftThrottle;
            else if (localPos.x > 0.5f) inputs.throttle = rightThrottle;
            else inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }

        static void Postfix(PropFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_inputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    // === HUD integration ===
    // The game's ThrottleGauge reads ControlInputs.throttle during HUDAppManager.Update, after the
    // player's bound throttle axis has already overwritten it (so it showed only the left lever).
    //  - Twin-engine + DualGauges: the gauge is cloned; each copy gets its own private ControlInputs
    //    fed from one engine's split throttle, so both are real game gauges (arc, colours, AB band).
    //  - Otherwise: the single gauge shows the combined throttle, with an "L xx%  R xx%" readout.
    [HarmonyPatch(typeof(ThrottleGauge), "Refresh")]
    internal static class ThrottleGaugePatch
    {
        static readonly FieldInfo f_inputs = AccessTools.Field(typeof(ThrottleGauge), "inputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(ThrottleGauge), "aircraft");
        static readonly FieldInfo f_reading = AccessTools.Field(typeof(ThrottleGauge), "throttleReading");
        static readonly FieldInfo f_label = AccessTools.Field(typeof(ThrottleGauge), "throttleLabel");
        static readonly FieldInfo f_pivot = AccessTools.Field(typeof(ThrottleGauge), "throttleReadingPivot");
        static readonly FieldInfo f_prev = AccessTools.Field(typeof(ThrottleGauge), "throttlePrev");
        static readonly FieldInfo f_gradient = AccessTools.Field(typeof(ThrottleGauge), "greenToOrangeGradient");
        static readonly FieldInfo f_fontSize = AccessTools.Field(typeof(HUDApp), "fontSize");
        static readonly FieldInfo f_fontMul = AccessTools.Field(typeof(HUDApp), "fontSizeMultiplier");
        static readonly FieldInfo f_regions = AccessTools.Field(typeof(ThrottleGauge), "throttleRegions");
        static readonly Type t_region = AccessTools.Inner(typeof(ThrottleGauge), "ThrottleRegion");
        static readonly FieldInfo f_rName = AccessTools.Field(t_region, "name");
        static readonly FieldInfo f_rShowName = AccessTools.Field(t_region, "showName");
        static readonly FieldInfo f_rShowPct = AccessTools.Field(t_region, "showPercent");
        static readonly FieldInfo f_rStart = AccessTools.Field(t_region, "start");
        static readonly FieldInfo f_rEnd = AccessTools.Field(t_region, "end");

        class GaugeState
        {
            public TextMeshProUGUI readout;      // L/R text (single-gauge mode)
            public ThrottleGauge clone;          // second gauge (dual mode)
            public string cloneLayout;
            public ControlInputs mainInputs = new ControlInputs();
            public ControlInputs cloneInputs = new ControlInputs();
            public string labelText, cloneLabelText;
            public bool dual;
            public float appliedScale = float.NaN;
        }

        static readonly Dictionary<ThrottleGauge, GaugeState> states = new Dictionary<ThrottleGauge, GaugeState>();
        static readonly HashSet<ThrottleGauge> clones = new HashSet<ThrottleGauge>();
        static bool driving; // re-entry guard while we refresh the clone

        internal static void Forget(ThrottleGauge g)
        {
            states.Remove(g);
            clones.Remove(g);
        }

        static bool IsLocal(ThrottleGauge g, out Aircraft ac)
        {
            ac = f_aircraft.GetValue(g) as Aircraft;
            return ac != null && ac == GetLocalAircraft();
        }

        static GaugeState State(ThrottleGauge g)
        {
            if (!states.TryGetValue(g, out var st)) { st = new GaugeState(); states[g] = st; }
            return st;
        }

        static void Prefix(ThrottleGauge __instance)
        {
            if (driving || clones.Contains(__instance)) return; // clone is driven from the original's postfix
            try
            {
                bool local = IsLocal(__instance, out var ac);
                var st = State(__instance);
                bool active = local && splitActive && cfgEnabled.Value;
                bool dual = active && cfgDualGauges.Value && LocalIsTwin(ac);

                if (dual)
                {
                    bool mirror = cfgDualLayout.Value != "Outer";
                    if (st.clone == null || st.cloneLayout != cfgDualLayout.Value)
                        CreateClone(__instance, ac, st, mirror);
                    if (!st.clone.gameObject.activeSelf) st.clone.gameObject.SetActive(true);
                    ApplyLayout(__instance, st, mirror);

                    // Mirror: original (right side of HUD) = right engine, mirrored clone = left.
                    // Outer:  original (inner arc) = left engine, outer clone = right.
                    st.mainInputs.throttle = mirror ? rightThrottle : leftThrottle;
                    st.cloneInputs.throttle = mirror ? leftThrottle : rightThrottle;
                    if (!st.dual) { f_inputs.SetValue(__instance, st.mainInputs); ForceRedraw(__instance); }
                    SetLabel(__instance, ref st.labelText, mirror ? "R" : "L");
                    st.dual = true;
                }
                else
                {
                    if (st.dual) // leave dual mode: give the gauge back the aircraft's real inputs
                    {
                        if (ac != null) f_inputs.SetValue(__instance, ac.GetInputs());
                        RestoreLabel(__instance, st.labelText);
                        ForceRedraw(__instance);
                        st.dual = false;
                    }
                    if (st.clone != null && st.clone.gameObject.activeSelf) st.clone.gameObject.SetActive(false);
                    if (active && f_inputs.GetValue(__instance) is ControlInputs inputs)
                        inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
                }
            }
            catch (Exception e) { Log?.LogWarning($"HUD gauge prefix error: {e.Message}"); }
        }

        static void Postfix(ThrottleGauge __instance)
        {
            if (driving || clones.Contains(__instance)) return;
            try
            {
                var st = State(__instance);
                if (st.dual && st.clone != null)
                {
                    // The clone stays in `clones`, so our patches skip it and only the game's own
                    // Refresh runs on it. (Taking it out of the set made the clone clone itself
                    // recursively -> stack overflow -> hard crash on spawn.)
                    driving = true;
                    try { st.clone.Refresh(); } finally { driving = false; }
                    FixMirroredText(st.clone);
                    SetLabel(st.clone, ref st.cloneLabelText, cfgDualLayout.Value != "Outer" ? "L" : "R");
                }
                UpdateReadout(__instance, st, !st.dual);
            }
            catch (Exception e) { Log?.LogWarning($"HUD gauge postfix error: {e.Message}"); }
        }

        static void ForceRedraw(ThrottleGauge g) => f_prev.SetValue(g, -1f);

        // Size/place the clone; re-run whenever OuterGaugeScale changes so it can be tuned live.
        static void ApplyLayout(ThrottleGauge original, GaugeState st, bool mirror)
        {
            float scale = mirror ? 1f : cfgOuterScale.Value;
            if (st.appliedScale == scale) return;
            st.appliedScale = scale;

            var t = st.clone.transform;
            t.localPosition = original.transform.localPosition;
            t.localRotation = original.transform.localRotation;
            Vector3 sc = original.transform.localScale;
            t.localScale = mirror ? new Vector3(-sc.x, sc.y, sc.z) : sc * scale;

            // Keep both arcs centred on the same point (the gauge's rotation pivot).
            var op = f_pivot.GetValue(original) as Transform;
            var cp = f_pivot.GetValue(st.clone) as Transform;
            if (op != null && cp != null) t.position += op.position - cp.position;
        }

        static void CreateClone(ThrottleGauge original, Aircraft ac, GaugeState st, bool mirror)
        {
            if (st.clone != null) { clones.Remove(st.clone); UnityEngine.Object.Destroy(st.clone.gameObject); }

            var go = UnityEngine.Object.Instantiate(original.gameObject, original.transform.parent, false);
            go.name = "SplitThrottle_SecondGauge";
            var clone = go.GetComponent<ThrottleGauge>();
            clones.Add(clone);
            // Our own L/R readout may have been cloned along with the gauge; remove it.
            var strayReadout = go.transform.Find("SplitThrottle_LR");
            if (strayReadout != null) UnityEngine.Object.Destroy(strayReadout.gameObject);

            clone.Initialize(ac);          // sets aircraft, theme hooks, gradient, visibility
            clone.RefreshSettings();       // fonts
            f_inputs.SetValue(clone, st.cloneInputs);
            st.cloneLabelText = null;
            // the template label may already carry our "L "/"R " prefix
            if (st.labelText != null && f_label.GetValue(clone) is TextMeshProUGUI cl) cl.text = st.labelText;

            st.clone = clone;
            st.appliedScale = float.NaN;
            ApplyLayout(original, st, mirror);
            st.cloneLayout = cfgDualLayout.Value;
            ForceRedraw(clone);
        }

        // A mirrored parent would render the clone's text backwards; flip the text objects back.
        static void FixMirroredText(ThrottleGauge clone)
        {
            if (clone.transform.localScale.x >= 0) return;
            foreach (var f in new[] { f_reading, f_label })
            {
                if (f.GetValue(clone) is TextMeshProUGUI txt)
                {
                    var s = txt.transform.localScale;
                    if (s.x > 0) txt.transform.localScale = new Vector3(-s.x, s.y, s.z);
                }
            }
        }

        static void SetLabel(ThrottleGauge g, ref string original, string side)
        {
            if (!(f_label.GetValue(g) is TextMeshProUGUI lbl)) return;
            if (original == null) original = lbl.text;
            string want = $"{side} {original}";
            if (lbl.text != want) lbl.text = want;
        }

        static void RestoreLabel(ThrottleGauge g, string original)
        {
            if (original != null && f_label.GetValue(g) is TextMeshProUGUI lbl) lbl.text = original;
        }

        static void UpdateReadout(ThrottleGauge gauge, GaugeState st, bool showWanted)
        {
            var reading = f_reading.GetValue(gauge) as TextMeshProUGUI;
            bool want = showWanted && splitActive && cfgEnabled.Value && cfgHudReadout.Value
                        && IsLocal(gauge, out _) && reading != null && reading.enabled;
            if (!want)
            {
                if (st.readout != null) st.readout.gameObject.SetActive(false);
                return;
            }
            if (st.readout == null) st.readout = CreateLabel(gauge, reading);
            if (!st.readout.gameObject.activeSelf) st.readout.gameObject.SetActive(true);

            // Directly under the gauge's own % reading (it slides along the arc with throttle).
            int fs = (int)f_fontSize.GetValue(gauge);
            float mul = (float)f_fontMul.GetValue(gauge);
            st.readout.fontSize = Mathf.Max(8, (int)((fs - 2) * mul));
            Vector3 basePos = gauge.transform.InverseTransformPoint(reading.transform.position);
            st.readout.rectTransform.localPosition = basePos
                + new Vector3(cfgHudOffsetX.Value, -st.readout.fontSize * 1.3f + cfgHudOffsetY.Value, 0f);
            st.readout.rectTransform.localRotation = Quaternion.identity;

            var grad = f_gradient.GetValue(gauge) as Gradient;
            var regions = f_regions.GetValue(gauge) as Array;
            st.readout.text = $"{Part("L", leftThrottle, grad, regions)}   {Part("R", rightThrottle, grad, regions)}";
        }

        // Mirrors ThrottleGauge's own text: inside one of the gauge's throttle regions
        // (e.g. the afterburner band) show that region's name / in-region percent.
        static string Part(string side, float v, Gradient g, Array regions)
        {
            Color c = g != null ? g.Evaluate(v) : Color.green;
            string txt = $"{v * 100f:0}%";
            if (regions != null)
            {
                foreach (var r in regions)
                {
                    if (r == null) continue;
                    float start = (float)f_rStart.GetValue(r), end = (float)f_rEnd.GetValue(r);
                    if (v < start || v > end) continue;
                    string t = "";
                    if ((bool)f_rShowName.GetValue(r)) t = (string)f_rName.GetValue(r) ?? "";
                    if ((bool)f_rShowPct.GetValue(r))
                    {
                        float pct = end - start > 0f ? (v - start) / (end - start) : 1f;
                        t += (t.Length > 0 ? " " : "") + $"{pct * 100f:0}%";
                    }
                    if (t.Length > 0) txt = t;
                }
            }
            return $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{side} {txt}</color>";
        }

        static TextMeshProUGUI CreateLabel(ThrottleGauge gauge, TextMeshProUGUI template)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, gauge.transform, false);
            go.name = "SplitThrottle_LR";
            for (int i = go.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
            var t = go.GetComponent<TextMeshProUGUI>();
            t.richText = true;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(200f, t.rectTransform.sizeDelta.y);
            return t;
        }
    }

    // Our clone isn't in HUDAppManager.apps; drop bookkeeping when gauges are destroyed.
    [HarmonyPatch(typeof(ThrottleGauge), "OnDestroy")]
    static class ThrottleGaugeDestroyPatch
    {
        static void Postfix(ThrottleGauge __instance) => ThrottleGaugePatch.Forget(__instance);
    }
}

public class SplitThrottleRunner : MonoBehaviour
{
    public static SplitThrottleRunner Instance;

    void Awake() { Instance = this; }
    void OnEnable() { StartCoroutine(UpdateLoop()); }

    IEnumerator UpdateLoop()
    {
        while (true)
        {
            yield return null;
            try { DoUpdate(); } catch { }
        }
    }

    static bool Held(ConfigEntry<KeyboardShortcut> e)
    {
        var k = e.Value;
        return k.MainKey != KeyCode.None && k.IsPressed();
    }

    void DoUpdate()
    {
        if (!Plugin.cfgEnabled.Value) return;
        if (GameManager.gameState != GameState.Multiplayer && GameManager.gameState != GameState.SinglePlayer) return;

        var aircraft = Plugin.GetLocalAircraft();
        bool wasActive = Plugin.splitActive;
        Plugin.splitActive = aircraft != null && !Plugin.IsDisabledAircraft(aircraft);

        if (!wasActive && Plugin.splitActive)
        {
            Plugin.leftThrottle = aircraft.GetInputs().throttle;
            Plugin.rightThrottle = Plugin.leftThrottle;
        }

        if (!Plugin.splitActive) return;

        // Keyboard controls (BepInEx config hotkeys)
        try
        {
            float step = Plugin.cfgKeyboardStep.Value;
            if (Held(Plugin.keyLeftUp))
                Plugin.leftThrottle = Mathf.Clamp01(Plugin.leftThrottle + step);
            if (Held(Plugin.keyLeftDown))
                Plugin.leftThrottle = Mathf.Clamp01(Plugin.leftThrottle - step);
            if (Held(Plugin.keyRightUp))
                Plugin.rightThrottle = Mathf.Clamp01(Plugin.rightThrottle + step);
            if (Held(Plugin.keyRightDown))
                Plugin.rightThrottle = Mathf.Clamp01(Plugin.rightThrottle - step);

            if (Plugin.keySync.Value.IsDown())
            {
                Plugin.leftThrottle = aircraft.GetInputs().throttle;
                Plugin.rightThrottle = Plugin.leftThrottle;
            }
        }
        catch { }

        // HOTAS direct axis reading
        try
        {
            var player = ReInput.players.GetPlayer(0);
            if (player != null)
            {
                int leftIdx = Plugin.cfgLeftAxisIndex.Value;
                int rightIdx = Plugin.cfgRightAxisIndex.Value;
                string nameFilter = Plugin.cfgControllerName.Value.ToUpperInvariant();

                foreach (var joystick in player.controllers.Joysticks)
                {
                    if (!joystick.name.ToUpperInvariant().Contains(nameFilter)) continue;
                    if (leftIdx < joystick.axisCount && rightIdx < joystick.axisCount)
                    {
                        float leftRaw = joystick.Axes[leftIdx].valueRaw;
                        float rightRaw = joystick.Axes[rightIdx].valueRaw;

                        if (Plugin.cfgInvertLeft.Value) leftRaw = -leftRaw;
                        if (Plugin.cfgInvertRight.Value) rightRaw = -rightRaw;

                        Plugin.leftThrottle = Mathf.Clamp01(0.5f * (leftRaw + 1f));
                        Plugin.rightThrottle = Mathf.Clamp01(0.5f * (rightRaw + 1f));
                        break;
                    }
                }
            }
        }
        catch { }
    }

    // Force HMD/HUD throttle indicator to track the actual split throttle state.
    // The engine FixedUpdate Postfix already writes (left+right)/2 back to
    // ControlInputs.throttle, but the game's input system overwrites that on
    // every Update with whatever the player's regular throttle key/axis is at,
    // so by the time the HMD draws it shows the player's input instead of the
    // engines' real power. LateUpdate runs after Update + coroutines but before
    // rendering, so re-syncing here makes the HMD match what the engines are
    // actually doing.
    void LateUpdate()
    {
        if (!Plugin.cfgEnabled.Value) return;
        if (!Plugin.splitActive) return;

        var aircraft = Plugin.GetLocalAircraft();
        if (aircraft == null) return;

        try
        {
            var inputs = aircraft.GetInputs();
            if (inputs != null)
                inputs.throttle = (Plugin.leftThrottle + Plugin.rightThrottle) * 0.5f;
        }
        catch { }
    }

    void OnGUI()
    {
        if (!Plugin.cfgEnabled.Value || !Plugin.splitActive || !Plugin.cfgShowOverlay.Value) return;

        float w = 120, h = 80;
        float x = Screen.width - w - 10;
        float y = Screen.height / 2f - h / 2f;

        GUI.Box(new Rect(x, y, w, h), "");
        var titleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold, fontSize = 11 };
        GUI.Label(new Rect(x, y + 2, w, 16), "SPLIT THROTTLE", titleStyle);

        float barW = 30, barH = 50, barY = y + 22;
        DrawBar(x + 20, barY, barW, barH, Plugin.leftThrottle, "L");
        DrawBar(x + w - 20 - barW, barY, barW, barH, Plugin.rightThrottle, "R");
    }

    void DrawBar(float x, float y, float w, float h, float value, string label)
    {
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(0.2f, 0.2f, 0.2f, 0.8f), 0, 0);
        float fillH = h * value;
        Color col = value < 0.8f ? Color.green : (value < 0.95f ? Color.yellow : Color.red);
        GUI.DrawTexture(new Rect(x, y + h - fillH, w, fillH), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(col.r, col.g, col.b, 0.8f), 0, 0);
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 10 };
        GUI.Label(new Rect(x, y + h, w, 14), $"{label} {value * 100:0}%", style);
    }
}
