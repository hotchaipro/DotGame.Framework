// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Globalization;

namespace DotGame.Framework.ContentPipeline.BuildTool;

/// <summary>
/// Parses '--name value' options and '--name' switches.  Every option must be
/// consumed by the command, so misspelled options fail the build instead of
/// being silently ignored.
/// </summary>
public sealed class CommandLineOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public static CommandLineOptions Parse(ReadOnlySpan<string> args)
    {
        var options = new CommandLineOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new ContentBuildException($"Unexpected argument '{arg}'. Options must have the form '--name value' or '--name'.");
            }
            string name = arg[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            if (!options._values.TryAdd(name, value))
            {
                throw new ContentBuildException($"Option '--{name}' is specified more than once.");
            }
        }
        return options;
    }

    public string GetRequired(string name)
    {
        string? value = this.GetOptional(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ContentBuildException($"Missing required option '--{name} <value>'.");
        }
        return value;
    }

    public string GetString(string name, string defaultValue = "") => this.GetOptional(name) ?? defaultValue;

    /// <summary>Gets a required file or directory path, normalized for the host.</summary>
    public string GetPath(string name) => ContentPaths.Normalize(this.GetRequired(name));

    /// <summary>
    /// Gets the path of an external tool, normalized for the host.  Falls back to
    /// <paramref name="defaultExecutable"/> (resolved from PATH) when the option is
    /// absent or empty.
    /// </summary>
    public string GetToolPath(string name, string defaultExecutable)
    {
        string path = ContentPaths.Normalize(this.GetString(name));
        return path.Length > 0 ? path : defaultExecutable;
    }

    public int GetInt32(string name, int defaultValue, int minimum, int maximum = int.MaxValue)
    {
        string? text = this.GetOptional(name);
        if (text is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
        {
            string range = maximum == int.MaxValue ? $">= {minimum}" : $"from {minimum} to {maximum}";
            throw new ContentBuildException($"Option '--{name}' must be an integer {range} (was '{text}').");
        }
        return value;
    }

    /// <summary>
    /// Gets one of a fixed set of values, compared without case.  Returns the
    /// matching entry of <paramref name="choices"/>.
    /// </summary>
    public string GetChoice(string name, string? defaultValue, params ReadOnlySpan<string> choices)
    {
        string? text = this.GetOptional(name);
        if (text is null)
        {
            return defaultValue ?? throw new ContentBuildException($"Missing required option '--{name} <{string.Join('|', choices.ToArray())}>'.");
        }
        foreach (string choice in choices)
        {
            if (string.Equals(text, choice, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }
        throw new ContentBuildException($"Option '--{name}' must be one of {string.Join(", ", choices.ToArray())} (was '{text}').");
    }

    public bool GetSwitch(string name)
    {
        this._consumed.Add(name);
        if (!this._values.TryGetValue(name, out string? value))
        {
            return false;
        }
        return value switch
        {
            null => true,
            _ when bool.TryParse(value, out bool result) => result,
            _ => throw new ContentBuildException($"Option '--{name}' is a switch; expected no value, 'true' or 'false' (was '{value}')."),
        };
    }

    /// <summary>Fails when an option was given that the command does not use.</summary>
    public void ThrowIfUnrecognized()
    {
        foreach (string name in this._values.Keys)
        {
            if (!this._consumed.Contains(name))
            {
                throw new ContentBuildException($"Unrecognized option '--{name}'.");
            }
        }
    }

    private string? GetOptional(string name)
    {
        this._consumed.Add(name);
        if (this._values.TryGetValue(name, out string? value) && value is null)
        {
            throw new ContentBuildException($"Option '--{name}' requires a value.");
        }
        return value;
    }
}
