// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;

namespace DotGame.Framework.ContentPipeline.BuildTool;

/// <summary>
/// Writes progress messages and MSBuild-canonical errors and warnings.
/// </summary>
public static class ContentBuildOutput
{
    /// <summary>The origin of errors and warnings that do not name a file.</summary>
    public static string Origin { get; set; } = "DotGame.Framework.ContentPipeline.BuildTool";

    public static void Message(string text) => Console.Out.WriteLine(text);

    public static void Warning(string? origin, string code, string text) =>
        Console.Out.WriteLine($"{(string.IsNullOrEmpty(origin) ? Origin : origin)}: warning {code}: {OneLine(text)}");

    public static void Error(string code, string text) =>
        Console.Error.WriteLine($"{Origin}: error {code}: {OneLine(text)}");

    public static string FormatElapsed(Stopwatch stopwatch) =>
        (stopwatch.ElapsedMilliseconds / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs a command and reports any failure as a single MSBuild-canonical error
    /// line, so MSBuild reports it as a first-class build error rather than only
    /// the generic "exited with code 1" error.
    /// </summary>
    /// <returns>The process exit code.</returns>
    public static int Run(string errorCode, Action command)
    {
        try
        {
            command();
            return 0;
        }
        catch (Exception ex)
        {
            Error(errorCode, ex.Message);
            if (ex is not ContentBuildException)
            {
                Message(ex.ToString());
            }
            return 1;
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
