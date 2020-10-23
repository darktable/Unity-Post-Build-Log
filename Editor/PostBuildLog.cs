using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Text.RegularExpressions;
using Debug = UnityEngine.Debug;

public class PostBuildLog : ScriptableObject {
	private enum STATUS
	{
		FOUND,
		END
	}

	private static readonly Regex BUILD_REPORT = new Regex(@"^Build Report$", RegexOptions.IgnoreCase);
	private static readonly Regex DASH_LINE = new Regex(@"^-+$");
	private static readonly Regex DEPENDENCIES_LIST = new Regex(@"^Mono dependencies included in the build$", RegexOptions.IgnoreCase);

	[MenuItem("Tools/Post Build Log/Test Build Report")]
	private static void TestBuildReport()
	{
		WriteBuildLog(Application.dataPath, "android");
	}

	[PostProcessBuild] // Requires Unity 3.5+
    private static void OnPostProcessBuildPlayer(BuildTarget target, string buildPath) {
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

	private static string ScenesInBuild()
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

    private static void WriteBuildLog(string buildPath, string target="") {
        //string editorLogFilePath = null;
        string[] pieces;

        bool winEditor = Application.platform == RuntimePlatform.WindowsEditor;
		STATUS buildMarker = STATUS.END;
		STATUS dependencyMarker = STATUS.END;

        if (winEditor) {
			pieces = new string[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Unity", "Editor", "Editor.log"};
        } else {
            pieces = new string[] { Environment.GetFolderPath(Environment.SpecialFolder.Personal),
				"Library","Logs","Unity","Editor.log"};
        }

		string editorLogFilePath = Path.Combine(pieces);

        if (!File.Exists(editorLogFilePath)) {
            Debug.LogWarning("Editor log file could not be found at: " + editorLogFilePath);
            return;
        }

        StringBuilder report = new StringBuilder();
		StringBuilder assemblies = new StringBuilder();

        using (StreamReader reader = new StreamReader(File.Open(editorLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) {
			string prevLine = null;
			string currentLine = null;
			int lineNum = 0;
            while (true) {
				lineNum++;
				prevLine = currentLine;
                currentLine = reader.ReadLine();

                if (currentLine == null) {
                    break;
                }

				if (BUILD_REPORT.IsMatch(currentLine)) {
					Debug.LogFormat("found a build report at line: {0}", lineNum);

					buildMarker = STATUS.FOUND;
                    report.Length = 0;
					report.AppendLine(prevLine);
                }
				else if (DEPENDENCIES_LIST.IsMatch(currentLine))
				{
					dependencyMarker = STATUS.FOUND;
					assemblies.Length = 0;
					assemblies.AppendLine(prevLine);
				}

				if (buildMarker == STATUS.FOUND)
				{
					report.AppendLine(currentLine);
				}
				else if (dependencyMarker == STATUS.FOUND)
				{
					assemblies.AppendLine(currentLine);
				}

				// end of report.
				if (buildMarker == STATUS.FOUND && DASH_LINE.IsMatch(currentLine))
				{
					buildMarker = STATUS.END;
				}
				else if (dependencyMarker == STATUS.FOUND && string.IsNullOrEmpty(currentLine))
				{
					dependencyMarker = STATUS.END;
				}
            }
        }

        if (report.Length == 0) {
			Debug.Log("no build report found in log.");
			return; // No builds have been run.
        }

        string outputPath;

		var filename = string.Format("build {0}.log", System.DateTime.UtcNow.ToString("s").Replace(':', '-'));

		if (target.StartsWith("standalone", StringComparison.InvariantCultureIgnoreCase) || target.StartsWith("android", StringComparison.InvariantCultureIgnoreCase)) {
			outputPath = Path.Combine(Path.GetDirectoryName(buildPath), filename);
        } else {
			outputPath = Path.Combine(buildPath, filename);
        }

        try {
			var output = new StringBuilder();
			output.AppendFormat("Build Report @ {0}\n\n", System.DateTime.Now.ToString("u"));
			output.Append(ScenesInBuild());
			output.Append(assemblies.ToString());
			output.Append(report.ToString());

			File.WriteAllText(outputPath, output.ToString());
        } catch (Exception e) {
			Debug.LogException(e);
			Debug.LogErrorFormat("Build log file could not be created for writing at: {0} for target {1}", outputPath, target);
        }
    }
}
