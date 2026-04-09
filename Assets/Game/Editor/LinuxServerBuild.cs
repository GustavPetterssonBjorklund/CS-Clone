using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Usable on linux, likely to error if used on windows
public static class LinuxServerBuild
{
    private const string BuildProfilePath = "Assets/Game/Settings/Build Profiles/Linux Server.asset";
    private const string MenuRoot = "Build/Linux Server/";
    private const string OutputDirectory = "/tmp/unity-build";
    private const string ExecutableName = "CS-Clone.x86_64";
    private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
    private const string QualitySettingsPath = "ProjectSettings/QualitySettings.asset";
    private const string RemoteDeviceAddressKey = "m_RemoteDeviceAddress";
    private const string RemoteDeviceUsernameKey = "m_RemoteDeviceUsername";
    private const string RemoteDevicePathKey = "m_PathOnRemoteDevice";
    private const string CommonSshOptions = "-o BatchMode=yes -o StrictHostKeyChecking=accept-new";

    private static string OutputPath => Path.Combine(OutputDirectory, ExecutableName);
    private static string OutputDirectoryContentsPath =>
        OutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";

    [InitializeOnLoadMethod]
    private static void InitializeBuildLocation()
    {
        PinBuildLocation();
    }

    [MenuItem(MenuRoot + "Use /tmp/unity-build")]
    public static void PinLinuxServerOutputPath()
    {
        PinBuildLocation();
        Debug.Log($"Linux server build output path set to '{OutputPath}'.");
    }

    [MenuItem(MenuRoot + "Build To /tmp/unity-build")]
    public static void BuildToPinnedLocation()
    {
        if (!BuildToPinnedLocationInternal())
        {
            return;
        }
    }

    [MenuItem(MenuRoot + "Upload Existing Build Over SSH")]
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

        EnsureOutputDirectoryExists();

        if (!TryResolveSshTarget(hostAlias, user, out string host, out string resolvedUser, out int? port))
        {
            host = hostAlias;
            resolvedUser = user;
            port = null;
        }

        string sshTarget = $"{resolvedUser}@{host}";
        if (!RunProcess("ssh", $"{BuildSshOptions(port)} {sshTarget} mkdir -p {ShellEscape(remotePath)}", out _, out string sshError))
        {
            Debug.LogError(
                $"Failed to create remote directory '{remotePath}' on '{sshTarget}'.\n{sshError}");
            return;
        }

        string destination = $"{sshTarget}:{remotePath.TrimEnd('/')}/";
        if (!RunProcess("scp", $"{BuildScpOptions(port)} -r {ShellEscape(OutputDirectoryContentsPath)} {ShellEscape(destination)}", out _, out string scpError))
        {
            Debug.LogError(
                $"Failed to upload Linux server build to '{destination}'.\n{scpError}");
            return;
        }

        Debug.Log($"Uploaded Linux server build from '{OutputDirectory}' to '{destination}'.");
    }

    [MenuItem(MenuRoot + "Build And Upload Over SSH")]
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
        if (!TryLoadBuildProfile(out BuildProfile profile))
        {
            return false;
        }

        EnsureOutputDirectoryExists();
        PinBuildLocation();

        BuildPlayerWithProfileOptions options = new BuildPlayerWithProfileOptions
        {
            buildProfile = profile,
            locationPathName = OutputPath,
            options = BuildOptions.None,
            assetBundleManifestPath = string.Empty,
        };

        using var renderPipelineOverride = DedicatedServerRenderPipelineOverride.Apply();

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

    private static void PinBuildLocation()
    {
        EditorUserBuildSettings.SetBuildLocation(BuildTarget.StandaloneLinux64, OutputPath);
    }

    private static void EnsureOutputDirectoryExists()
    {
        Directory.CreateDirectory(OutputDirectory);
    }

    private static bool TryLoadBuildProfile(out BuildProfile profile)
    {
        profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(BuildProfilePath);
        if (profile != null)
        {
            return true;
        }

        Debug.LogError($"Linux server build profile not found at '{BuildProfilePath}'.");
        return false;
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
            TryReadValue(line, RemoteDeviceAddressKey, ref host);
            TryReadValue(line, RemoteDeviceUsernameKey, ref user);
            TryReadValue(line, RemoteDevicePathKey, ref remotePath);
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

    private static bool TryResolveSshTarget(string hostAlias, string configuredUser, out string host, out string user, out int? port)
    {
        host = hostAlias;
        user = configuredUser;
        port = null;

        if (!RunProcess("ssh", $"-G {ShellEscape(hostAlias)}", out string stdout, out string stderr))
        {
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Debug.LogWarning(
                    $"Could not resolve SSH alias '{hostAlias}' with 'ssh -G'. " +
                    "Falling back to direct SSH invocation.\n" +
                    stderr.Trim());
            }

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

    private sealed class DedicatedServerRenderPipelineOverride : IDisposable
    {
        private readonly string graphicsSettingsContents;
        private readonly string qualitySettingsContents;
        private bool disposed;

        private DedicatedServerRenderPipelineOverride(
            string graphicsSettingsContents,
            string qualitySettingsContents)
        {
            this.graphicsSettingsContents = graphicsSettingsContents;
            this.qualitySettingsContents = qualitySettingsContents;
        }

        public static DedicatedServerRenderPipelineOverride Apply()
        {
            string graphicsSettingsContents = File.ReadAllText(GraphicsSettingsPath);
            string qualitySettingsContents = File.ReadAllText(QualitySettingsPath);

            File.WriteAllText(GraphicsSettingsPath, SanitizeGraphicsSettings(graphicsSettingsContents));
            File.WriteAllText(QualitySettingsPath, SanitizeQualitySettings(qualitySettingsContents));
            AssetDatabase.Refresh();

            Debug.Log(
                "Temporarily disabled SRP assets for the dedicated server build to avoid " +
                "URP shader/script deserialization errors.");

            return new DedicatedServerRenderPipelineOverride(
                graphicsSettingsContents,
                qualitySettingsContents);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            File.WriteAllText(GraphicsSettingsPath, graphicsSettingsContents);
            File.WriteAllText(QualitySettingsPath, qualitySettingsContents);
            AssetDatabase.Refresh();

            Debug.Log("Restored SRP assets after the dedicated server build.");
        }

        private static string SanitizeGraphicsSettings(string contents)
        {
            string sanitized = contents.Replace(
                "  m_CustomRenderPipeline: {fileID: 11400000, guid: 4b83569d67af61e458304325a23e5dfd, type: 2}",
                "  m_CustomRenderPipeline: {fileID: 0}");

            const string urpGlobalSettingsEntry =
                "  m_RenderPipelineGlobalSettingsMap:\n" +
                "    UnityEngine.Rendering.Universal.UniversalRenderPipeline: {fileID: 11400000, guid: 18dc0cd2c080841dea60987a38ce93fa, type: 2}";

            sanitized = sanitized.Replace(
                urpGlobalSettingsEntry,
                "  m_RenderPipelineGlobalSettingsMap: {}");

            return sanitized;
        }

        private static string SanitizeQualitySettings(string contents)
        {
            return contents
                .Replace(
                    "    customRenderPipeline: {fileID: 11400000, guid: 5e6cbd92db86f4b18aec3ed561671858, type: 2}",
                    "    customRenderPipeline: {fileID: 0}")
                .Replace(
                    "    customRenderPipeline: {fileID: 11400000, guid: 4b83569d67af61e458304325a23e5dfd, type: 2}",
                    "    customRenderPipeline: {fileID: 0}");
        }
    }

    private static string BuildSshOptions(int? port)
    {
        return BuildRemoteOptions(port, "-p");
    }

    private static string BuildScpOptions(int? port)
    {
        return BuildRemoteOptions(port, "-P");
    }

    private static string BuildRemoteOptions(int? port, string portFlag)
    {
        var builder = new StringBuilder(CommonSshOptions);
        if (port.HasValue)
        {
            builder.Append($" {portFlag} {port.Value}");
        }

        return builder.ToString();
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
