// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Xunit;

namespace DotGame.ContentPipeline.Testing;

/// <summary>
/// The real external tools, for the tests that need them. A test asks for each tool and skips
/// with a reason when one isn't installed.
/// </summary>
internal static class RealTools
{
    private static readonly ConcurrentDictionary<string, string> Identities = new(StringComparer.Ordinal);

    /// <summary>Finds a tool on PATH or in the Vulkan SDK, or skips the test.</summary>
    public static string Require(params string[] names)
    {
        foreach (string name in names)
        {
            if (Find(name) is { } path)
            {
                return path;
            }
        }

        Assert.Skip($"'{names[0]}' isn't installed, or isn't on PATH (pipeline/install installs the content tools).");
        return "";
    }

    /// <summary>
    /// The spirv2dxil checked in for this machine (native/spirv2dxil/&lt;rid&gt;/), as the pipeline
    /// finds it, or skips the test.
    /// </summary>
    public static string Spirv2Dxil()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        string path = Path.Combine(Paths.FrameworkRoot, "native", "spirv2dxil", $"{os}-{arch}", OperatingSystem.IsWindows() ? "spirv2dxil.exe" : "spirv2dxil");
        if (!File.Exists(path))
        {
            Assert.Skip($"There's no spirv2dxil for {os}-{arch} in native/spirv2dxil (the native-spirv2dxil workflow builds them).");
        }

        return path;
    }

    public static string? Find(string name)
    {
        var directories = new List<string>();
        if (Environment.GetEnvironmentVariable("VULKAN_SDK") is { Length: > 0 } sdk)
        {
            directories.Add(Path.Combine(sdk, "Bin"));
            directories.Add(Path.Combine(sdk, "bin"));
        }

        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        string fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string directory in directories)
        {
            string path = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>A short id of a tool's exact build: goldens hold for one build of a tool.</summary>
    public static string Identity(string toolPath)
    {
        return Identities.GetOrAdd(toolPath, static path =>
        {
            using var file = File.OpenRead(path);
            return $"{Path.GetFileNameWithoutExtension(path)}-{Convert.ToHexStringLower(SHA256.HashData(file))[..12]}";
        });
    }
}

internal static class GoldenFiles
{
    public static string Framework => Path.Combine(Paths.FrameworkRoot, "test", "DotGame.Framework.ContentPipeline.Tests");
}

/// <summary>
/// The output versions in ContentVersions.props: a golden holds for one version.
/// </summary>
internal static class ContentVersions
{
    public static string Of(string property)
    {
        foreach (string? root in new[] { Paths.FrameworkRoot, Paths.LibraryRootOrNull })
        {
            if (root is null)
            {
                continue;
            }

            string text = File.ReadAllText(Path.Combine(root, "pipeline", "tool", "ContentVersions.props"));
            int start = text.IndexOf($"<{property}>", StringComparison.Ordinal);
            if (start >= 0)
            {
                start += property.Length + 2;
                return $"v{text[start..text.IndexOf('<', start)]}";
            }
        }

        throw new ArgumentException($"No version '{property}'.", nameof(property));
    }
}

/// <summary>
/// Golden hashes: <c>Goldens\goldens.txt</c> beside a test project's sources, one
/// <c>case|identity|hash</c> a line. The identity says what the hash holds for: the content type's
/// version (ContentVersions.props), and the exact tools when their output isn't otherwise stable.
/// </summary>
/// <remarks>
/// A mismatch at an identity on file means the output changed with no version bump: bump the
/// version in ContentVersions.props, so caches don't keep serving the old output, and record the
/// new hash. An identity not on file skips the test, naming the tool. Run with
/// <c>DOTGAME_UPDATE_GOLDENS=1</c> to record (this appends, for the identity at hand).
/// </remarks>
internal static class Goldens
{
    private static readonly Lock WriteLock = new();

    public static void Check(string testProjectDir, string caseName, string identity, byte[] content)
    {
        Check(testProjectDir, caseName, identity, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    public static void Check(string testProjectDir, string caseName, string identity, string hash)
    {
        string file = Path.Combine(testProjectDir, "Goldens", "goldens.txt");
        string prefix = $"{caseName}|{identity}|";
        string? recorded = null;
        lock (WriteLock)
        {
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        recorded = line[prefix.Length..];
                    }
                }
            }

            if (Environment.GetEnvironmentVariable("DOTGAME_UPDATE_GOLDENS") == "1")
            {
                if (recorded != hash)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    string[] kept = File.Exists(file) ? File.ReadAllLines(file) : [];
                    File.WriteAllLines(file, kept.Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)).Append(prefix + hash).Order(StringComparer.Ordinal));
                }

                return;
            }
        }

        if (recorded is null)
        {
            Assert.Skip($"No golden hash for '{caseName}' with {identity}. Run with DOTGAME_UPDATE_GOLDENS=1 to record one, once the output looks right.");
        }

        Assert.True(recorded == hash, $"'{caseName}' changed with {identity}: was {recorded}, now {hash}. If that's intended, bump its version in ContentVersions.props, so caches don't keep the old output, and record the new hash with DOTGAME_UPDATE_GOLDENS=1.");
    }
}
