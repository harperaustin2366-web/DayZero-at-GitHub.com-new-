// Headless build helpers. Nobody opens this project in the Unity editor, so every Player
// setting the Android build depends on is (re)applied from code here, in addition to
// ProjectSettings/ProjectSettings.asset.
//
// Unity Build Automation (Android target -> Advanced settings):
//   Pre-export method name:  DayZero.EditorTools.BuildScript.PreExport
//   Post-export method name: DayZero.EditorTools.BuildScript.PostExport   (optional)
// Command line / Unity CLI (any machine with the editor + Android module):
//   Unity -batchmode -quit -projectPath . -buildTarget Android \
//         -executeMethod DayZero.EditorTools.BuildScript.BuildAndroid [-output Builds/Android/DayZero.apk]
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace DayZero.EditorTools
{
    public static class BuildScript
    {
        public const string COMPANY = "Austin Harper";
        public const string PRODUCT = "DayZero";
        public const string VERSION = "0.15.0a";
        public const int VERSION_CODE = 10;
        public const string APP_ID = "com.austinharper.dayzero";
        public const string BOOT_SCENE = "Assets/Scenes/Boot.unity";
        public const string DEFAULT_APK = "Builds/Android/DayZero.apk";
        public const string MAP_SHADER = "Assets/Resources/Shaders/DayZeroMap.shader";
        /// <summary>Shaders MapView loads by name at runtime; forced into the build (GraphicsSettings > Always Included Shaders).</summary>
        static readonly string[] RUNTIME_SHADERS = { "Hidden/Internal-Colored", "Sprites/Default", "UI/Default" };

        /// <summary>Apply every Player / build setting the Android build needs. Safe to call repeatedly.</summary>
        public static void ApplyAndroidSettings()
        {
            PlayerSettings.companyName = COMPANY;
            PlayerSettings.productName = PRODUCT;
            PlayerSettings.bundleVersion = VERSION;
            PlayerSettings.Android.bundleVersionCode = VERSION_CODE;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, APP_ID);

            // Landscape only, rotating between left and right (Godot: handheld/orientation = sensor landscape).
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.useAnimatedAutorotation = true;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            // Unity 6.3 supports Android 7.1 (API 25) and newer; 25 is the lowest value it accepts.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto; // highest installed
            // Newtonsoft deserialises the save classes by reflection: keep stripping minimal (see Assets/link.xml).
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Minimal);
            PlayerSettings.runInBackground = false;

            EnsureBootSceneListed();
            // Build #3 (v0.11.2a code 3) failed writing unity_builtin_extra after this forced built-in
            // shaders (Hidden/Internal-Colored etc.) into Always Included Shaders. DayZeroMap.shader ships via
            // Resources/, and the Shader.Find fallbacks are built-in, so the GraphicsSettings edit is no longer made.
            Debug.Log(string.Format("[BuildScript] Android settings applied: {0} {1} ({2}) id={3} IL2CPP ARM64 minSdk={4}",
                PRODUCT, VERSION, VERSION_CODE, APP_ID, PlayerSettings.Android.minSdkVersion));
        }

        static void EnsureBootSceneListed()
        {
            if (!File.Exists(BOOT_SCENE))
            {
                Debug.LogWarning("[BuildScript] " + BOOT_SCENE + " is missing; the game still starts through Bootstrap's RuntimeInitializeOnLoadMethod if any scene is built.");
                return;
            }
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Count > 0 && scenes[0].path == BOOT_SCENE && scenes[0].enabled) return;
            scenes.RemoveAll(s => s.path == BOOT_SCENE);
            scenes.Insert(0, new EditorBuildSettingsScene(BOOT_SCENE, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>Add the map shader (and the fallbacks MapView finds with Shader.Find) to
        /// GraphicsSettings' Always Included Shaders, so they are compiled into the player even though
        /// no scene material references them.</summary>
        static void EnsureAlwaysIncludedShaders()
        {
            var gs = GraphicsSettings.GetGraphicsSettings();
            if (gs == null) { Debug.LogWarning("[BuildScript] GraphicsSettings not found; cannot add Always Included Shaders."); return; }
            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray) { Debug.LogWarning("[BuildScript] m_AlwaysIncludedShaders not found."); return; }
            var want = new System.Collections.Generic.List<Shader>();
            var map = AssetDatabase.LoadAssetAtPath<Shader>(MAP_SHADER);
            if (map != null) want.Add(map); else Debug.LogWarning("[BuildScript] " + MAP_SHADER + " did not import as a shader.");
            foreach (var n in RUNTIME_SHADERS) { var sh = Shader.Find(n); if (sh != null) want.Add(sh); }
            bool changed = false;
            foreach (var sh in want)
            {
                bool has = false;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == sh) { has = true; break; }
                if (has) continue;
                int at = list.arraySize;
                list.InsertArrayElementAtIndex(at);
                list.GetArrayElementAtIndex(at).objectReferenceValue = sh;
                changed = true;
                Debug.Log("[BuildScript] Always Included Shaders += " + sh.name);
            }
            if (changed) { so.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssets(); }
        }

        static string[] EnabledScenes()
        {
            var list = EditorBuildSettings.scenes.Where(s => s.enabled && File.Exists(s.path)).Select(s => s.path).ToArray();
            return list.Length > 0 ? list : new[] { BOOT_SCENE };
        }

        /// <summary>Unity Build Automation pre-export hook (runs after script compilation, before the player build).</summary>
        public static void PreExport()
        {
            ApplyAndroidSettings();
            AssetDatabase.SaveAssets();
        }

        /// <summary>Unity Build Automation post-export hook. Build Automation passes the export path.</summary>
        public static void PostExport(string exportPath)
        {
            Debug.Log("[BuildScript] Exported to " + exportPath);
        }

        /// <summary>Build an APK. Use with -executeMethod; optional "-output path/to.apk" argument.</summary>
        public static void BuildAndroid()
        {
            string output = ArgValue("-output") ?? DEFAULT_APK;
            ApplyAndroidSettings();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.buildAppBundle = output.EndsWith(".aab", StringComparison.OrdinalIgnoreCase);

            string dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var options = new BuildPlayerOptions
            {
                scenes = EnabledScenes(),
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log(string.Format("[BuildScript] Android build {0}: {1} bytes, {2} errors, {3} warnings -> {4}",
                summary.result, summary.totalSize, summary.totalErrors, summary.totalWarnings, output));
            if (Application.isBatchMode)
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
