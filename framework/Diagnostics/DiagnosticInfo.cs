// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// The one place the framework reads information through reflection: type names, and enum values
/// for the Debug self-checks (<see cref="FeatureSwitches.IsDebug"/>). Reflection hinders trimming and costs time, so it's banned everywhere else
/// (BannedSymbols.txt), and the only approved callers are diagnostics: traces, exception
/// messages, crash reports, Debug-only self-checks and the like. Don't use it to decide anything.
/// </summary>
internal static class DiagnosticInfo
{
#pragma warning disable RS0030 // The approved place for reflection, for diagnostics only
    /// <summary>
    /// The name of the value's type, or null for a null value.
    /// </summary>
    public static string? TypeName(
        object? value)
    {
        return value?.GetType().Name;
    }

    /// <summary>
    /// The namespace-qualified name of the value's type, or null for a null value.
    /// </summary>
    public static string? FullTypeName(
        object? value)
    {
        return value?.GetType().FullName;
    }

    /// <summary>
    /// The name of <typeparamref name="T"/>.
    /// </summary>
    public static string TypeName<T>()
    {
        return typeof(T).Name;
    }

    /// <summary>
    /// The number of values <typeparamref name="TEnum"/> defines, for Debug-only checks that a
    /// count kept by hand still matches the enum.
    /// </summary>
    public static int EnumValueCount<TEnum>()
        where TEnum : struct, System.Enum
    {
        return System.Enum.GetValues<TEnum>().Length;
    }
#pragma warning restore RS0030
}
