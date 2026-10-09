// Builds the Windows game: menu "The Last Cast > Build Windows", or from the
// command line with -executeMethod LastCast.EditorTools.BuildGame.Windows.

using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LastCast.EditorTools
{
    public static class BuildGame
    {
        public const string Output = "Builds/Windows/TheLastCast.exe";

        [MenuItem("The Last Cast/Build Windows")]
        public static void Windows()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };
            KeepRuntimeShaderVariants();
            // The models may have been copied in before ModelImport compiled: import them again with its settings.
            if (AssetDatabase.IsValidFolder(ModelImport.Folder))
                AssetDatabase.ImportAsset(ModelImport.Folder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            PlayerSettings.productName = "The Last Cast";
            PlayerSettings.companyName = "The Last Cast";
            PlayerSettings.bundleVersion = GameVersion.Current;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"Build {s.result}: {s.outputPath} ({s.totalSize / 1e6:0.0} MB, {s.totalErrors} errors) in {s.totalTime}");
            if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>
        /// The game makes its materials in code, so Unity can't see that it uses GPU instancing
        /// (the scenery) or linear fog, and would strip those shader variants from the build.
        /// </summary>
        static void KeepRuntimeShaderVariants()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
            if (settings == null) return;
            var so = new SerializedObject(settings);
            void Set(string name, int value)
            {
                var p = so.FindProperty(name);
                if (p == null) { Debug.LogWarning($"GraphicsSettings has no {name}"); return; }
                if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = value != 0;
                else p.intValue = value;
            }
            Set("m_InstancingStripping", 2); // keep all instancing variants
            Set("m_FogStripping", 1); // custom: keep the fog modes below
            Set("m_FogKeepLinear", 1);
            Set("m_FogKeepExp", 1);
            Set("m_FogKeepExp2", 1);
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
