using UnityEditor;
using UnityEngine;

// One-off command-line build entry point (Unity -batchmode -executeMethod CiAndroidBuild.Build).
// Not wired into any CI workflow - just a manual "give me an APK" helper.
public static class CiAndroidBuild
{
    public static void Build()
    {
        var scenes = new[]
        {
            "Assets/Scenes/Main Menu.unity",
            "Assets/Scenes/Game.unity",
        };

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = "Builds/Android/BlobRush.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        var summary = report.summary;
        Debug.Log($"CiAndroidBuild result={summary.result} totalErrors={summary.totalErrors} totalWarnings={summary.totalWarnings} outputPath={summary.outputPath}");

        if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }
}
