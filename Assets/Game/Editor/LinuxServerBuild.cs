using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class LinuxServerBuild
{
    private const string BuildProfilePath = "Assets/Game/Settings/Build Profiles/Linux Server.asset";
    private const string OutputDirectory = "/tmp/unity-build";
    private const string ExecutableName = "CS-Clone.x86_64";

    private static string OutputPath => Path.Combine(OutputDirectory, ExecutableName);

    [InitializeOnLoadMethod]
    private static void InitializeBuildLocation()
    {
        EditorUserBuildSettings.SetBuildLocation(BuildTarget.StandaloneLinux64, OutputPath);
    }

    [MenuItem("Build/Linux Server/Use /tmp/unity-build")]
    public static void PinLinuxServerOutputPath()
    {
        EditorUserBuildSettings.SetBuildLocation(BuildTarget.StandaloneLinux64, OutputPath);
        Debug.Log($"Linux server build output path set to '{OutputPath}'.");
    }

    [MenuItem("Build/Linux Server/Build To /tmp/unity-build")]
    public static void BuildToPinnedLocation()
    {
        if (!BuildToPinnedLocationInternal())
        {
            return;
        }
    }

    [MenuItem("Build/Linux Server/Upload Existing Build Over SSH")]
    public static void UploadExistingBuild()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            Debug.LogError($"Build output directory '{OutputDirectory}' does not exist.");
            return;
        }

        if (!TryReadRemoteSettings(out string hostAlias, out string user, out string remotePath))
        {
            Debug.LogError($"Could not read Linux server SSH settings from '{BuildProfilePath}'.");
            return;
        }

        Directory.CreateDirectory(OutputDirectory);

        if (!TryResolveSshTarget(hostAlias, user, out string host, out string resolvedUser, out int port))
        {
            host = hostAlias;
            resolvedUser = user;
            port = 22;
        }

        string sshTarget = $"{resolvedUser}@{host}";
        string sshOptions = BuildSshOptions(port);
        string scpOptions = BuildScpOptions(port);
        if (!RunProcess("ssh", $"{sshOptions} {sshTarget} mkdir -p {ShellEscape(remotePath)}", out _, out string sshError))
        {
            Debug.LogError(
                $"Failed to create remote directory '{remotePath}' on '{sshTarget}'.\n{sshError}");
            return;
        }

        string sourcePath = OutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
        string destination = $"{sshTarget}:{remotePath.TrimEnd('/')}/";
        if (!RunProcess("scp", $"{scpOptions} -r {ShellEscape(sourcePath)} {ShellEscape(destination)}", out _, out string scpError))
        {
            Debug.LogError(
                $"Failed to upload Linux server build to '{destination}'.\n{scpError}");
            return;
        }

        Debug.Log($"Uploaded Linux server build from '{OutputDirectory}' to '{destination}'.");
    }

    [MenuItem("Build/Linux Server/Build And Upload Over SSH")]
    public static void BuildAndUpload()
    {
        if (!BuildToPinnedLocationInternal())
        {
            return;
        }

        UploadExistingBuild();
    }

    private static bool BuildToPinnedLocationInternal()
    {
        BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(BuildProfilePath);
        if (profile == null)
        {
            Debug.LogError($"Linux server build profile not found at '{BuildProfilePath}'.");
            return false;
        }

        Directory.CreateDirectory(OutputDirectory);
        EditorUserBuildSettings.SetBuildLocation(BuildTarget.StandaloneLinux64, OutputPath);

        BuildPlayerWithProfileOptions options = new BuildPlayerWithProfileOptions
        {
            buildProfile = profile,
            locationPathName = OutputPath,
            options = BuildOptions.None,
            assetBundleManifestPath = string.Empty,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError(
                $"Linux server build failed with result {summary.result}. " +
                $"See the Unity console for details. Output path: '{OutputPath}'.");
            return false;
        }

        Debug.Log(
            $"Linux server build completed: '{OutputPath}' " +
            $"({summary.totalSize} bytes, {summary.totalTime.TotalSeconds:F1}s).");
        return true;
    }

    private static bool TryReadRemoteSettings(out string host, out string user, out string remotePath)
    {
        host = string.Empty;
        user = string.Empty;
        remotePath = string.Empty;

        if (!File.Exists(BuildProfilePath))
        {
            return false;
        }

        foreach (string line in File.ReadLines(BuildProfilePath))
        {
            TryReadValue(line, "m_RemoteDeviceAddress", ref host);
            TryReadValue(line, "m_RemoteDeviceUsername", ref user);
            TryReadValue(line, "m_PathOnRemoteDevice", ref remotePath);
        }

        return !string.IsNullOrWhiteSpace(host)
            && !string.IsNullOrWhiteSpace(user)
            && !string.IsNullOrWhiteSpace(remotePath);
    }

    private static void TryReadValue(string line, string key, ref string value)
    {
        string prefix = $"{key}:";
        int index = line.IndexOf(prefix, System.StringComparison.Ordinal);
        if (index < 0)
        {
            return;
        }

        string candidate = line[(index + prefix.Length)..].Trim();
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
        }
    }

    private static bool RunProcess(string fileName, string arguments, out string stdout, out string stderr)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        stdout = process.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static bool TryResolveSshTarget(string hostAlias, string configuredUser, out string host, out string user, out int port)
    {
        host = hostAlias;
        user = configuredUser;
        port = 22;

        if (!RunProcess("ssh", $"-G {ShellEscape(hostAlias)}", out string stdout, out _))
        {
            return false;
        }

        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("hostname ", System.StringComparison.Ordinal))
            {
                host = trimmed["hostname ".Length..].Trim();
            }
            else if (trimmed.StartsWith("user ", System.StringComparison.Ordinal))
            {
                user = trimmed["user ".Length..].Trim();
            }
            else if (trimmed.StartsWith("port ", System.StringComparison.Ordinal)
                     && int.TryParse(trimmed["port ".Length..].Trim(), out int parsedPort))
            {
                port = parsedPort;
            }
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            user = configuredUser;
        }

        return !string.IsNullOrWhiteSpace(host);
    }

    private static string BuildSshOptions(int port)
    {
        return $"-F /dev/null -o BatchMode=yes -o StrictHostKeyChecking=accept-new -p {port}";
    }

    private static string BuildScpOptions(int port)
    {
        return $"-F /dev/null -o BatchMode=yes -o StrictHostKeyChecking=accept-new -P {port}";
    }

    private static string ShellEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
        return builder.ToString();
    }
}
