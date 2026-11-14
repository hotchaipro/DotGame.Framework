// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool;

public static class ContentPaths
{
    /// <summary>
    /// Normalizes a path received from MSBuild.  Strips surrounding quotes (tool
    /// paths arrive pre-quoted) and, on macOS and Linux, converts '\' separators:
    /// MSBuild emits item metadata such as OutputRelativeDir with backslashes on
    /// every platform, but '\' is a valid file name character there.
    /// </summary>
    public static string Normalize(string path)
    {
        path = path.Trim().Trim('"');
        return OperatingSystem.IsWindows() ? path : path.Replace('\\', '/');
    }

    /// <summary>Creates the directory that will contain <paramref name="path"/>, if any.</summary>
    public static void CreateParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Reads a list of paths separated by ';' or line breaks.  Lists are passed in
    /// files because they can exceed the Windows command-line length limit.
    /// </summary>
    public static List<string> ReadPathList(string listFile)
    {
        if (!File.Exists(listFile))
        {
            throw new ContentBuildException($"Input list file not found: '{listFile}'");
        }
        return File.ReadAllText(listFile)
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToList();
    }

    /// <summary>
    /// Reads an encode command template written by the targets.  Templates are
    /// passed in files to avoid nested command-line quoting.
    /// </summary>
    public static string ReadCommandTemplate(string templateFile)
    {
        if (!File.Exists(templateFile))
        {
            throw new ContentBuildException($"Encode command template file not found: '{templateFile}'");
        }
        string template = File.ReadAllText(templateFile).Trim();
        if (template.Length == 0)
        {
            throw new ContentBuildException($"Encode command template file is empty: '{templateFile}'");
        }
        return template;
    }

    /// <summary>
    /// Ordering for names derived from file names: case-insensitive, then
    /// case-sensitive as a tiebreaker, independent of the build machine's culture.
    /// </summary>
    public static readonly IComparer<string> NameComparer = Comparer<string>.Create(static (a, b) =>
    {
        int result = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return result != 0 ? result : StringComparer.Ordinal.Compare(a, b);
    });

    /// <summary>
    /// Fails when two inputs map to the same name.  Names are compared without
    /// case because the descriptors are looked up case-insensitively.
    /// </summary>
    public static void ThrowIfDuplicateNames(IEnumerable<string> paths, string kind)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!seen.TryAdd(name, path))
            {
                throw new ContentBuildException($"'{seen[name]}' and '{path}' both produce the {kind} name '{name}'; {kind} names come from file names and must be unique.");
            }
        }
    }

    /// <summary>
    /// Runs a file operation, retrying with exponential backoff while the file is
    /// locked by another process (typically an anti-virus or indexing service
    /// scanning a file that was just written, or a parallel build node).
    /// </summary>
    public static T RetryWhileLocked<T>(Func<T> operation, string description)
    {
        const int MaxAttempts = 6;
        int delayMilliseconds = 40;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException ex) when (attempt < MaxAttempts && IsSharingViolation(ex))
            {
                ContentBuildOutput.Message($"{description} encountered a transient file lock (attempt {attempt}/{MaxAttempts}). Retrying in {delayMilliseconds}ms...");
                Thread.Sleep(delayMilliseconds);
                delayMilliseconds *= 2;
            }
        }
    }

    public static void RetryWhileLocked(Action operation, string description) =>
        RetryWhileLocked(() => { operation(); return true; }, description);

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as HRESULTs.
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        return ex.HResult is SharingViolation or LockViolation;
    }
}
