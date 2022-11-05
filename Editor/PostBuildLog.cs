using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public class PostBuildLog : IPostprocessBuildWithReport
{
    private enum Status
    {
        Found,
        End,
    }

#if UNITY_EDITOR_WIN
    private static readonly string k_LogPath = Path.Combine(
        new string[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log" });
#else
    private static readonly string k_LogPath = Path.Combine(
        new string[]{ Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "Unity", "Editor.log" });
#endif
    private static readonly string[] k_Newlines = { "\r\n", "\r", "\n" };

    private static readonly Regex k_BuildReport = new Regex(@"^Build Report$", RegexOptions.IgnoreCase);
    private static readonly Regex k_DashLine = new Regex(@"^-+$");

    private static readonly Regex k_DependenciesList =
        new Regex(@"^Mono dependencies included in the build$", RegexOptions.IgnoreCase);

    public int callbackOrder => int.MaxValue;

    public void OnPostprocessBuild(BuildReport report)
    {
        var result = report.summary.result;

        if (result == BuildResult.Failed || result == BuildResult.Cancelled)
        {
            return;
        }

        EditorCoroutineUtility.StartCoroutineOwnerless(WriteBuildLog(report));
    }

    [MenuItem("Tools/Post Build Log/Test Build Report")]
    private static void TestBuildReport()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(WriteBuildLog(null));
    }

    internal static void ScenesInBuild(List<string> sceneList)
    {
        int sceneCount = SceneManager.sceneCountInBuildSettings;
        for (var i = 0; i < sceneCount; i++)
        {
            string sceneName = SceneUtility.GetScenePathByBuildIndex(i);

            sceneList.Add(sceneName);
        }
    }

    internal static bool GetBuildReportFromEditorLog(StringBuilder logBuilder, string logPath)
    {
        var buildMarker = Status.End;
        var dependencyMarker = Status.End;

        if (!File.Exists(logPath))
        {
            Debug.LogWarning($"Editor log file could not be found at: {logPath}");
            return false;
        }

        var report = new StringBuilder();
        var assemblies = new StringBuilder();

        using (var reader =
               new StreamReader(File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            string currentLine = null;
            var lineNum = 0;
            while (true)
            {
                lineNum++;
                string prevLine = currentLine;
                currentLine = reader.ReadLine();

                if (currentLine == null)
                {
                    break;
                }

                if (k_BuildReport.IsMatch(currentLine))
                {
                    // Debug.LogFormat("found a build report at line: {0}", lineNum);

                    buildMarker = Status.Found;
                    report.Length = 0;
                    report.AppendLine(prevLine);
                }
                else if (k_DependenciesList.IsMatch(currentLine))
                {
                    dependencyMarker = Status.Found;
                    assemblies.Length = 0;
                    assemblies.AppendLine(prevLine);
                }

                if (buildMarker == Status.Found)
                {
                    report.AppendLine(currentLine);
                }
                else if (dependencyMarker == Status.Found)
                {
                    assemblies.AppendLine(currentLine);
                }

                // end of report.
                if (buildMarker == Status.Found && k_DashLine.IsMatch(currentLine))
                {
                    buildMarker = Status.End;
                }
                else if (dependencyMarker == Status.Found && string.IsNullOrEmpty(currentLine))
                {
                    dependencyMarker = Status.End;
                }
            }
        }

        logBuilder.Append(assemblies);
        logBuilder.Append(report);
        return true;
    }

    private static IEnumerator WriteBuildLog(BuildReport buildReport)
    {
        // This is a hack, but on windows you have to wait
        // 1 frame after a build finishes for the
        // log file to get written out. (and delayCall doesn't work).
        yield return null;

        BuildTarget target;
        string buildPath;
        var report = new StringBuilder();

        if (buildReport != null)
        {
            target = buildReport.summary.platform;
            buildPath = buildReport.summary.outputPath;

            GetBuildReportFromEditorLog(report, k_LogPath);
        }
        else
        {
            // Probably running a test, so pretend we're windows.
            target = BuildTarget.StandaloneWindows64;
            buildPath = Application.dataPath;

            if (!GetBuildReportFromEditorLog(report, k_LogPath))
            {
                Debug.Log("No build report found. Checking previous log file...");
                string prevLogPath = Path.Combine(Path.GetDirectoryName(k_LogPath), "Editor-prev.log");

                if (!GetBuildReportFromEditorLog(report, prevLogPath))
                {
                    Debug.Log("no build report found in log.");
                    yield break; // No builds have been run.
                }
            }
        }

        string outputPath;

        var filename = $"build {DateTime.UtcNow.ToString("s").Replace(':', '-')}.log";

        switch (target)
        {
            case BuildTarget.Android:
            case BuildTarget.StandaloneLinux64:
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
            case BuildTarget.StandaloneOSX:
                outputPath = Path.Combine(Path.GetDirectoryName(buildPath), filename);
                break;
            default:
                outputPath = Path.Combine(buildPath, filename);
                break;
        }

        try
        {
            var output = new StringBuilder();
            output.AppendFormat("Build Report @ {0:u}\n\n", DateTime.UtcNow);

            var scenes = new List<string>();
            ScenesInBuild(scenes);

            if (scenes.Count != 0)
            {
                output.Append("Scenes included in the build\n");
                foreach (var scene in scenes)
                {
                    output.AppendFormat("{0}\n", scene);
                }
            }

            output.Append(report);

            File.WriteAllText(outputPath, output.ToString());
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogErrorFormat("Build log file could not be created for writing at: {0} for target {1}", outputPath,
                target);
        }
    }
}
