using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

// Batch-mode Windows player build for testing the client without a phone (Unity -batchmode -executeMethod
// CiWindowsBuild.Build). Uses the Mono scripting backend so it builds in a minute or two instead of IL2CPP's
// many, and restores the project's own backend setting afterwards so it doesn't leave a settings change behind.
public static class CiWindowsBuild
{
    public static void Build()
    {
        var target = NamedBuildTarget.Standalone;
        var previousBackend = PlayerSettings.GetScriptingBackend(target);
        PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.Mono2x);

        try
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main Menu.unity", "Assets/Scenes/Game.unity" },
                locationPathName = "Builds/Windows/BlobRush.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });

            var summary = report.summary;
            Debug.Log("CiWindowsBuild result=" + summary.result + " totalErrors=" + summary.totalErrors + " outputPath=" + summary.outputPath);
            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
        finally
        {
            PlayerSettings.SetScriptingBackend(target, previousBackend);
        }
    }
}
