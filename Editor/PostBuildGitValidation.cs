using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class PostBuildGitValidation : IPostprocessBuildWithReport
{
    // Handy git commands:
    // rev-parse --short HEAD  // get hash of current revision

    // git diff --cached --compact-summary   // staged files that haven't been committed.
    //  .gitmodules | 3 +++

    // git ls-files --exclude-standard // everything in the repo.
    // git ls-files -d     // deleted
    // git ls-files -m     // modified
    // git ls-files -o --exclude-standard    // files that need to be added

    private const string k_GitFilename = "git";
    private const string k_IgnoredFiles = "ls-files -i -o --exclude-standard";
    private const string k_UnversionedFiles = "ls-files -o --exclude-standard";
    private const string k_TestCommand = "--version";
    private const string k_DotGit = ".git";

    private static readonly Regex k_AssetEntry = new Regex(@"^.*% (Assets/.*)$", RegexOptions.IgnoreCase);

#if UNITY_EDITOR_WIN
    private static readonly string k_LogPath = Path.Combine(
        new string[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log" });
#else
    private static readonly string k_LogPath = Path.Combine(
        new string[]{ Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "Unity", "Editor.log" });
#endif

    public static bool CheckGitAfterBuild { get; private set; } = true;
    public int callbackOrder => int.MaxValue;

    public void OnPostprocessBuild(BuildReport report)
    {
        var result = report.summary.result;

        if (result == BuildResult.Failed || result == BuildResult.Cancelled)
        {
            return;
        }

        var assetList = new List<string>();
        if (CheckGitAfterBuild && ProjectUsesGit() && GetAssetListFromBuildReport(assetList, report))
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(CheckGit(assetList));
        }
    }

    [MenuItem("Tools/Post Build Log/Find Unversioned Files")]
    private static void TestGitUnversioned()
    {
        var assetList = new List<string>();

        if (!GetAssetListFromEditorLog(assetList, k_LogPath))
        {
            string prevLogPath = Path.Combine(Path.GetDirectoryName(k_LogPath), "Editor-prev.log");

            if (!GetAssetListFromEditorLog(assetList, prevLogPath))
            {
                Debug.LogWarning("no builds have been run yet.");
                return;
            }
        }

        PostBuildLog.ScenesInBuild(assetList);

        EditorCoroutineUtility.StartCoroutineOwnerless(CheckGit(assetList));
    }

    private static bool ProjectUsesGit()
    {
        // check to see if the git command is available.
        using var process = CreateGitProcess();

        process.StartInfo.Arguments = k_TestCommand;

        try
        {
            process.Start();
            process.WaitForExit();

            string error = process.StandardError.ReadToEnd();

            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }

        // search for a .git directory or .git file in the Assets directory or above.

        string assetPath = Application.dataPath;
        while (!string.IsNullOrWhiteSpace(assetPath))
        {
            string gitPath = Path.Combine(assetPath, k_DotGit);

            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return true;
            }

            assetPath = Path.GetDirectoryName(assetPath);
        }

        return false;
    }

    private static bool GetAssetListFromBuildReport(List<string> assetList, BuildReport buildReport)
    {
        var result = buildReport.summary.result;

        if (result == BuildResult.Cancelled || result == BuildResult.Failed)
        {
            return false;
        }

        var packedAssets = buildReport.packedAssets;
        foreach (var packedAsset in packedAssets)
        {
            foreach (var info in packedAsset.contents)
            {
                assetList.Add(info.sourceAssetPath);
            }
        }

        return assetList.Count > 0;
    }

    private static bool GetAssetListFromEditorLog(List<string> assetList, string logPath)
    {
        var logBuilder = new StringBuilder();
        if (!PostBuildLog.GetBuildReportFromEditorLog(logBuilder, logPath))
        {
            return false;
        }

        using var reportReader = new StringReader(logBuilder.ToString());

        while (reportReader.Peek() != -1)
        {
            string line = reportReader.ReadLine();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = k_AssetEntry.Match(line);

            if (match.Groups.Count == 2)
            {
                string asset = match.Groups[1].Value;

                if (string.IsNullOrEmpty(asset))
                {
                    continue;
                }

                assetList.Add(asset);
            }
        }

        return true;
    }

    private static IEnumerator CheckGit(List<string> assets)
    {
        // TODO: check submodules
        // TODO: check Packages

        if (assets.Count == 0)
        {
            Debug.Log("no assets to check.");
            yield break; // No builds have been run.
        }

        var buildAssets = new HashSet<string>();

        var visitedDirectories = new HashSet<string> { "Assets" };

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset))
            {
                continue;
            }

            // for each line in build report
            // see if it matches regex
            // if so add the matched substring to assets
            if (!File.Exists(asset))
            {
                // Unity generates some assets that are added to the build.
                // (Unity does this with movies)

                // doesn't exist: 'Resources/unity_builtin_extra'
                // doesn't exist: 'Built-in Cubemap:'

                Debug.LogWarning($"doesn't exist: '{asset}'");
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

        // every asset in the build (except Packages) should be included in buildAssets hashset.

        // ignored files in build:
        var ignoredFilesInBuild = new HashSet<string>();

        var output = new List<string>();

        yield return EditorCoroutineUtility.StartCoroutineOwnerless(RunGitCommand(k_IgnoredFiles, output));

        foreach (string line in output)
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

        foreach (string line in output)
        {
            if (buildAssets.Remove(line))
            {
                unversionedFilesInBuild.Add(line);
            }
        }

        var stringBuilder = new StringBuilder();

        if (ignoredFilesInBuild.Count > 0)
        {
            stringBuilder.AppendLine($"total ignored files in build: {ignoredFilesInBuild.Count}");
            foreach (string file in ignoredFilesInBuild)
            {
                stringBuilder.AppendLine(file);
            }

            Debug.Log(stringBuilder);
        }

        stringBuilder.Clear();

        if (unversionedFilesInBuild.Count > 0)
        {
            stringBuilder.AppendLine($"total unversioned files in build: {unversionedFilesInBuild.Count}");
            foreach (string file in unversionedFilesInBuild)
            {
                stringBuilder.AppendLine(file);
            }

            Debug.LogWarning(stringBuilder);
        }

        // TODO: Show a dialog box and add the assets to git?
    }

    private static Process CreateGitProcess()
    {
        var process = new Process();
        process.StartInfo.FileName = k_GitFilename;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

        return process;
    }

    private static IEnumerator RunGitCommand(string command, List<string> output)
    {
        using var process = CreateGitProcess();
        process.StartInfo.Arguments = command;
        process.Start();

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            string standardOutput = process.StandardOutput.ReadLine();

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
