using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    public static void BuildWindows()
    {
        var scenes = System.Array.ConvertAll(EditorBuildSettings.scenes, s => s.path);
        var report = BuildPipeline.BuildPlayer(scenes, "Builds/Game.exe", BuildTarget.StandaloneWindows64, BuildOptions.None);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
