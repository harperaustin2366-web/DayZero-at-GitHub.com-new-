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

namespace DayZero.EditorTools
{
    public static class BuildScript
    {
        public const string COMPANY = "Austin Harper";
        public const string PRODUCT = "DayZero";
        public const string VERSION = "0.11.0a";
        public const int VERSION_CODE = 1;
        public const string APP_ID = "com.austinharper.dayzero";
        public const string BOOT_SCENE = "Assets/Scenes/Boot.unity";
        public const string DEFAULT_APK = "Builds/Android/DayZero.apk";

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
