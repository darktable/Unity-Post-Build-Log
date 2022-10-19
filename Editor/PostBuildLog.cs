using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public class PostBuildLog : ScriptableObject
{
    // rev-parse --short HEAD  // get hash of current revision

    // git diff --cached --compact-summary   // staged files that haven't been committed.
    //  .gitmodules | 3 +++

    // git ls-files --exclude-standard // everything in the repo.
    // git ls-files -d     // deleted
    // git ls-files -m     // modified
    // git ls-files -o --exclude-standard    // files that need to be added

    private enum Status
    {
        Found,
        End
    }

    private const string k_GitFilename = "git";
    private const string k_IgnoredFiles = "ls-files -i -o --exclude-standard";
    private const string k_UnversionedFiles = "ls-files -o --exclude-standard";

    private static readonly string[] k_Newlines = { "\r\n", "\r", "\n" };

    private static readonly Regex k_AssetEntry = new Regex(@"^.*% (Assets/.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex k_BuildReport = new Regex(@"^Build Report$", RegexOptions.IgnoreCase);
    private static readonly Regex k_DashLine = new Regex(@"^-+$");

    private static readonly Regex k_DependenciesList =
        new Regex(@"^Mono dependencies included in the build$", RegexOptions.IgnoreCase);

    [MenuItem("Tools/Post Build Log/Test Build Report")]
    static void TestBuildReport()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(WriteBuildLog(Application.dataPath, "android"));
    }

    [MenuItem("Tools/Post Build Log/Find Git Unversioned")]
    static void TestGitUnversioned()
    {
        var buildLog = new StringBuilder();
        AppendBuildLog(buildLog);

        var scenes = new StringBuilder();
        ScenesInBuild(scenes);

        EditorCoroutineUtility.StartCoroutineOwnerless(CheckGit(buildLog, scenes));
    }

    [PostProcessBuild] // Requires Unity 3.5+
    static void OnPostProcessBuildPlayer(BuildTarget target, string buildPath)
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(WriteBuildLog(buildPath, target.ToString()));
    }

    static void ScenesInBuild(StringBuilder report)
    {
        report.Append("Scenes included in the build\n");

        int sceneCount = SceneManager.sceneCountInBuildSettings;
        for (int i = 0; i < sceneCount; i++)
        {
            var sceneName = SceneUtility.GetScenePathByBuildIndex(i);

            report.AppendFormat("{0}\n", sceneName);
        }
    }

    private static void AppendBuildLog(StringBuilder output)
    {
        //string editorLogFilePath = null;
        string[] pieces;

        bool winEditor = Application.platform == RuntimePlatform.WindowsEditor;
        Status buildMarker = Status.End;
        Status dependencyMarker = Status.End;

        if (winEditor)
        {
            pieces = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unity", "Editor", "Editor.log"
            };
        }
        else
        {
            pieces = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Library", "Logs", "Unity", "Editor.log"
            };
        }

        string editorLogFilePath = Path.Combine(pieces);

        if (!File.Exists(editorLogFilePath))
        {
            Debug.LogWarning("Editor log file could not be found at: " + editorLogFilePath);
            return;
        }

        StringBuilder report = new StringBuilder();
        StringBuilder assemblies = new StringBuilder();

        using (StreamReader reader =
               new StreamReader(File.Open(editorLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            string currentLine = null;
            int lineNum = 0;
            while (true)
            {
                lineNum++;
                var prevLine = currentLine;
                currentLine = reader.ReadLine();

                if (currentLine == null)
                {
                    break;
                }

                if (k_BuildReport.IsMatch(currentLine))
                {
                    Debug.LogFormat("found a build report at line: {0}", lineNum);

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

        output.Append(assemblies);
        output.Append(report);
    }

    private static IEnumerator WriteBuildLog(string buildPath, string target = "")
    {
        // This is a hack, but on windows you have to wait
        // 1 frame after a build finishes for the
        // log file to get written out. (and delayCall doesn't work).
        yield return null;
        
        var report = new StringBuilder();

        AppendBuildLog(report);

        if (report.Length == 0)
        {
            Debug.Log("no build report found in log.");
            yield break; // No builds have been run.
        }

        string outputPath;

        var filename = $"build {DateTime.UtcNow.ToString("s").Replace(':', '-')}.log";

        if (target.StartsWith("standalone", StringComparison.InvariantCultureIgnoreCase) ||
            target.StartsWith("android", StringComparison.InvariantCultureIgnoreCase))
        {
            outputPath = Path.Combine(Path.GetDirectoryName(buildPath), filename);
        }
        else
        {
            outputPath = Path.Combine(buildPath, filename);
        }

        try
        {
            var output = new StringBuilder();
            output.AppendFormat("Build Report @ {0:u}\n\n", DateTime.Now);

            ScenesInBuild(output);

            // output.Append(ScenesInBuild());
            // output.Append(assemblies.ToString());
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

    static IEnumerator CheckGit(StringBuilder report, StringBuilder scenes)
    {
        // TODO: check submodules
        // TODO: check Packages

        var buildAssets = new HashSet<string>();

        var reader = new StringReader(report.ToString());

        var visitedDirectories = new HashSet<string>();
        visitedDirectories.Add("Assets");

        while (reader.Peek() != -1)
        {
            var line = reader.ReadLine();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // var lines = buildReport.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);

            // for each line in buildreport
            // see if it matches regex
            // if so add the matched substring to assets
            var match = k_AssetEntry.Match(line);

            if (match.Groups.Count == 2)
            {
                var asset = match.Groups[1].Value;

                if (string.IsNullOrEmpty(asset))
                {
                    continue;
                }

                if (!File.Exists(asset))
                {
                    // Unity generates some assets that are added to the build.
                    // (Unity does this with movies)

                    Debug.LogWarningFormat("doesn't exist: {0}", asset);
                    continue;
                }

                // TODO: skip files that are under git submodules

                string directoryName = asset;
                while (true)
                {
                    directoryName = Path.GetDirectoryName(directoryName);
                    if (string.IsNullOrEmpty(directoryName))
                    {
                        break;
                    }

#if UNITY_EDITOR_WIN
                    directoryName = directoryName.Replace(Path.DirectorySeparatorChar, '/');
#endif

                    if (visitedDirectories.Contains(directoryName))
                    {
                        continue;
                    }

                    buildAssets.Add($"{directoryName}.meta");
                    visitedDirectories.Add(directoryName);
                }

                buildAssets.Add(asset);
                buildAssets.Add($"{asset}.meta");
            }
        }

        var scenesList = scenes.ToString().Split(
            k_Newlines,
            StringSplitOptions.None
        );

        foreach (var scene in scenesList)
        {
            buildAssets.Add(scene);
            buildAssets.Add($"{scene}.meta");
        }

        // every asset in the build (except Packages) should be included in buildAssets hashset.

        // ignored files in build:
        var ignoredFilesInBuild = new HashSet<string>();

        var output = new List<string>();

        yield return EditorCoroutineUtility.StartCoroutineOwnerless(RunGitCommand(k_IgnoredFiles, output));

        foreach (var line in output)
        {
            if (buildAssets.Remove(line))
            {
                ignoredFilesInBuild.Add(line);
            }
        }

        // unversioned files in the build:
        var unversionedFilesInBuild = new HashSet<string>();

        output.Clear();
        yield return EditorCoroutineUtility.StartCoroutineOwnerless(RunGitCommand(k_UnversionedFiles, output));

        foreach (var line in output)
        {
            if (buildAssets.Remove(line))
            {
                unversionedFilesInBuild.Add(line);
            }
        }

        if (ignoredFilesInBuild.Count > 0)
        {
            Debug.Log($"total ignored files in build: {ignoredFilesInBuild.Count}");
            foreach (var file in ignoredFilesInBuild)
            {
                Debug.Log($"ignored in build: {file}");
            }
        }

        if (unversionedFilesInBuild.Count > 0)
        {
            Debug.Log($"total unversioned files in build: {unversionedFilesInBuild.Count}");
            foreach (var file in unversionedFilesInBuild)
            {
                Debug.Log($"unversioned in build: {file}");
            }
        }

        // TODO: Show a dialog box and add the assets to git?
    }

    private static IEnumerator RunGitCommand(string command, List<string> output)
    {
        using var process = new Process();
        process.StartInfo.FileName = k_GitFilename;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

        process.StartInfo.Arguments = command;
        process.Start();

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var standardOutput = process.StandardOutput.ReadLine();

            if (standardOutput == null)
            {
                yield break;
            }

            output.Add(standardOutput);

            if (stopwatch.ElapsedMilliseconds > 500)
            {
                yield return null;
                stopwatch.Restart();
            }
        }
    }
}