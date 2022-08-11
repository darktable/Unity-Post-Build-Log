#define GIT_CHECK

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public class PostBuildLog : ScriptableObject
{
    static readonly Regex k_AssetEntry = new Regex(@"^.*% (Assets/.*)$", RegexOptions.IgnoreCase);
    static readonly Regex k_GitError = new Regex("^error:.*'(.*)'.*$", RegexOptions.IgnoreCase);

    const string k_GitLs = "ls-files --error-unmatch{0}";

    enum Status
    {
        Found,
        End
    }

    static readonly Regex k_BuildReport = new Regex(@"^Build Report$", RegexOptions.IgnoreCase);
    static readonly Regex k_DashLine = new Regex(@"^-+$");
    static readonly Regex k_DependenciesList = new Regex(@"^Mono dependencies included in the build$", RegexOptions.IgnoreCase);

    [MenuItem("Tools/Post Build Log/Test Build Report")]
    static void TestBuildReport()
    {
        WriteBuildLog(Application.dataPath, "android");
    }

    [PostProcessBuild] // Requires Unity 3.5+
    static void OnPostProcessBuildPlayer(BuildTarget target, string buildPath)
    {
        // This is a hack, but on windows you have to wait
        // 1 frame after a build finishes for the
        // log file to get written out. (and delayCall doesn't work).
        EditorApplication.update = CallbackFunc;

        void CallbackFunc()
        {
            WriteBuildLog(buildPath, target.ToString());
            EditorApplication.update -= CallbackFunc;
        }
    }

    static string ScenesInBuild()
    {
        StringBuilder scenesList = new StringBuilder();

        scenesList.AppendLine("Scenes included in the build");

        int sceneCount = SceneManager.sceneCountInBuildSettings;
        for (int i = 0; i < sceneCount; i++)
        {
            var sceneName = SceneUtility.GetScenePathByBuildIndex(i);

            scenesList.AppendLine(sceneName);
        }

        return scenesList.ToString();
    }

    static void WriteBuildLog(string buildPath, string target = "")
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

        using (StreamReader reader = new StreamReader(File.Open(editorLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
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

        if (report.Length == 0)
        {
            Debug.Log("no build report found in log.");
            return; // No builds have been run.
        }

        string outputPath;

        var filename = $"build {DateTime.UtcNow.ToString("s").Replace(':', '-')}.log";

        if (target.StartsWith("standalone", StringComparison.InvariantCultureIgnoreCase) || target.StartsWith("android", StringComparison.InvariantCultureIgnoreCase))
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
            output.Append(ScenesInBuild());
            output.Append(assemblies.ToString());
            output.Append(report);

            File.WriteAllText(outputPath, output.ToString());


        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogErrorFormat("Build log file could not be created for writing at: {0} for target {1}", outputPath, target);
        }

#if GIT_CHECK
        CheckGit(new StringReader(report.ToString()));
#endif
    }

    static void CheckGit(StringReader reader)
    {
        // TODO: check submodules
        // TODO: check Packages

        var buildAssets = new List<string>();

        var unversioned = new List<string>();

        var directoryHashset = new HashSet<string>();

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

                var path = Path.GetDirectoryName(asset).Replace('\\', '/');

                if (!path.Equals("Assets", StringComparison.CurrentCultureIgnoreCase))
                {
                    directoryHashset.Add(path);
                }

                buildAssets.Add(asset);
            }
        }

        var arguments = new List<string>();

        var stringBuilder = new StringBuilder();

        foreach (var asset in buildAssets)
        {
            // Verify the file actually exists
            if (!File.Exists(asset))
            {
                // Unity generates some assets that are added to the build.
                // (Unity does this with movies)

                //Debug.LogWarningFormat("doesn't exist: {0}", asset);
                continue;
            }

            // also check for .meta files
            var line = string.Format(" \"{0}\" \"{0}.meta\"", asset);

            if (line.Length + stringBuilder.Length > 2000)
            {
                arguments.Add(stringBuilder.ToString());
                stringBuilder.Clear();
            }

            stringBuilder.Append(line);
        }

        arguments.Add(stringBuilder.ToString());
        stringBuilder.Clear();

        foreach (var dir in directoryHashset)
        {
            var line = $" \"{dir}.meta\"";

            if (line.Length + stringBuilder.Length > 2000)
            {
                arguments.Add(stringBuilder.ToString());
                stringBuilder.Clear();
            }

            stringBuilder.Append(line);
        }

        arguments.Add(stringBuilder.ToString());
        stringBuilder.Clear();

        foreach (var line in arguments)
        {
            using var process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;

            //process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.StartInfo.Arguments = string.Format(k_GitLs, line);
            process.Start();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var err = process.StandardError.ReadToEnd();

                var results = err.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var result in results)
                {
                    var match = k_GitError.Match(result);

                    if (match.Groups.Count == 2)
                    {
                        var unver = match.Groups[1].Value;

                        //Debug.LogWarningFormat("unversioned asset in build: {0}", unver);

                        unversioned.Add(unver);
                    }
                }
            }
        }

        if (unversioned.Count == 0)
        {
            Debug.Log("No unversioned assets in build!");
        }
        else
        {
            unversioned.Sort();

            foreach (var asset in unversioned)
            {
                Debug.LogWarningFormat("unversioned asset in build: {0}", asset);
            }
        }

        // TODO: Show a dialog box and add the assets to git?
    }
}
