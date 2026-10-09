// Adds the packages The Last Cast needs (JSON, Input System, multiplayer) through
// the Package Manager API. Run headless, WITHOUT -quit:
//   Unity.exe -batchmode -projectPath <project> -executeMethod ProjectBootstrap.PackageInstaller.Install
// It quits by itself when the packages are resolved.

using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace ProjectBootstrap
{
    public static class PackageInstaller
    {
        static readonly string[] PackagesToAdd =
        {
            "com.unity.nuget.newtonsoft-json",
            "com.unity.inputsystem",
            "com.unity.netcode.gameobjects", // multiplayer: networking between players
            "com.unity.services.multiplayer", // multiplayer: hosting, join codes and the game list
        };

        const double TimeoutSeconds = 600;
        static AddAndRemoveRequest request;
        static double deadline;

        public static void Install()
        {
            Debug.Log($"[PackageInstaller] Adding: {string.Join(", ", PackagesToAdd)}");
            request = Client.AddAndRemove(packagesToAdd: PackagesToAdd);
            deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (request == null) return;
            if (!request.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    EditorApplication.update -= Poll;
                    Debug.LogError("[PackageInstaller] Timed out waiting for the Package Manager.");
                    EditorApplication.Exit(2);
                }
                return;
            }
            EditorApplication.update -= Poll;
            if (request.Status == StatusCode.Success)
            {
                Debug.Log($"[PackageInstaller] Resolved: {string.Join(", ", request.Result.Select(p => $"{p.name}@{p.version}"))}");
                // The game reads the keyboard and mouse through the Input System: make sure it's switched on.
                var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
                if (settings != null)
                {
                    var so = new SerializedObject(settings);
                    var handler = so.FindProperty("activeInputHandler");
                    if (handler != null && handler.intValue == 0)
                    {
                        handler.intValue = 2; // both the old and the new input
                        so.ApplyModifiedProperties();
                    }
                }
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError($"[PackageInstaller] Failed: {request.Error?.message}");
                EditorApplication.Exit(1);
            }
        }
    }
}
