using System;
using System.Globalization;
using System.Diagnostics;
using BetterPerformance.Core;
using UnityEngine;

namespace BetterPerformance
{
    internal static class GraphicsTelemetry
    {
        // Explicit allowlist: no PlayerPrefs dump, mod secrets, or per-frame reflection.
        private static readonly GraphicsSettingInt[] Integers = {
            GraphicsSettingInt.SimulationDistance, GraphicsSettingInt.LOD,
            GraphicsSettingInt.Target3DResolutionVertical, GraphicsSettingInt.UpscalingAlgorithm,
            GraphicsSettingInt.FpsLimit, GraphicsSettingInt.Vegetation, GraphicsSettingInt.Lights,
            GraphicsSettingInt.ShadowQuality, GraphicsSettingInt.PointLights,
            GraphicsSettingInt.PointLightShadows, GraphicsSettingInt.SSAO, GraphicsSettingInt.ClothQuality };
        private static readonly GraphicsSettingBool[] Booleans = {
            GraphicsSettingBool.Vsync, GraphicsSettingBool.DistantShadows, GraphicsSettingBool.Tesselation,
            GraphicsSettingBool.Bloom, GraphicsSettingBool.DepthOfField, GraphicsSettingBool.MotionBlur,
            GraphicsSettingBool.ChromaticAberration, GraphicsSettingBool.SunShafts,
            GraphicsSettingBool.SoftParticles, GraphicsSettingBool.AntiAliasing,
            GraphicsSettingBool.AnisotropicTextures };
        private static readonly string[] ActiveKeys = Keys("graphics.active.");
        private static readonly string[] PlayerKeys = Keys("graphics.player_raw.");

        internal static void Observe(CaptureSession session, string source)
        {
            long started = Stopwatch.GetTimestamp();
            double elapsed = session.Elapsed;
            void Value(string key, string value) => session.Configurations.Observe(key, value, elapsed, source);
            void Number(string key, double value) => Value(key, value.ToString("R", CultureInfo.InvariantCulture));
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsDedicated())
                { Value("graphics.status", "not_applicable_dedicated_server"); return; }
                var manager = GraphicsSettingsManager.Instance;
                if (manager == null) { Value("graphics.status", "unavailable"); return; }
                // Raw player values may be overridden by a preset/background mode. Keep both views.
                GraphicsSettingsState active = manager.ActiveSettings;
                GraphicsSettingsState player = manager.CurrentPlayerSettingsRaw;
                ObserveState(active, ActiveKeys, session, elapsed, source);
                ObserveState(player, PlayerKeys, session, elapsed, source);
                Number("graphics.preset_id", manager.CurrentPresetID);
                Number("graphics.runtime.width", Screen.width);
                Number("graphics.runtime.height", Screen.height);
                Number("graphics.runtime.fullscreen_mode", (int)Screen.fullScreenMode);
                Number("graphics.runtime.target_fps", Application.targetFrameRate);
                Number("graphics.runtime.vsync_count", QualitySettings.vSyncCount);
                Number("graphics.runtime.lod_bias", QualitySettings.lodBias);
                Value("graphics.runtime.focused", Application.isFocused ? "true" : "false");
                Value("graphics.status", "available");
            }
            catch
            {
                // A diagnostic failure must never escape a graphics event into the game.
                session.RecordProbeFailure();
                Value("graphics.status", "unavailable");
            }
            finally
            {
                session.Book.Record(Metric.GraphicsObservation,
                    (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            }
        }

        private static void ObserveState(GraphicsSettingsState state, string[] keys, CaptureSession session,
            double elapsed, string source)
        {
            int index = 0;
            foreach (var setting in Integers)
                session.Configurations.Observe(keys[index++], state.GetValue(setting).ToString(CultureInfo.InvariantCulture), elapsed, source);
            foreach (var setting in Booleans)
                session.Configurations.Observe(keys[index++], state.GetValue(setting) ? "true" : "false", elapsed, source);
        }

        private static string[] Keys(string prefix)
        {
            var keys = new string[Integers.Length + Booleans.Length];
            int index = 0;
            foreach (var setting in Integers) keys[index++] = prefix + setting;
            foreach (var setting in Booleans) keys[index++] = prefix + setting;
            return keys;
        }
    }
}
