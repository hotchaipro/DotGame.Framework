// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;

using DotGame.Framework.Content;

namespace DotGame.Framework.Platform.Content;

/// <summary>
/// A single registration in a <see cref="ContentManifest"/>.
/// </summary>
internal readonly record struct ContentManifestEntry
{
    internal ContentManifestEntry(
        string key,
        string? subKey,
        string path,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        this.Key = key;
        this.SubKey = subKey;
        this.Path = path;
        this.Properties = properties;
    }

    /// <summary>
    /// Normalized logical content key (forward slashes; case
    /// preserved for diagnostics, compared case-insensitively).
    /// </summary>
    public readonly string Key;

    /// <summary>
    /// Optional single-token sub-identifier, or
    /// <see langword="null"/> for single-output assets.
    /// </summary>
    internal readonly string? SubKey;

    /// <summary>
    /// Normalized physical relative path produced by the build
    /// pipeline (forward slashes, no leading slash).
    /// </summary>
    public readonly string Path;

    /// <summary>
    /// Optional advisory metadata bag contributed by the build
    /// pipeline (or by hand-authored manifests) and carried alongside
    /// the entry. Keys are content-type specific (e.g. music entries
    /// may carry <c>DurationSeconds</c>); see the relevant content
    /// type's <c>*Properties</c> helper for the well-known keys.
    /// Values are always invariant-culture strings; semantic parsing
    /// is owned by the consumer. <see langword="null"/> when no
    /// properties were supplied (the common case).
    /// </summary>
    public readonly IReadOnlyDictionary<string, string>? Properties;

    /// <summary>
    /// Convenience accessor that returns <see langword="true"/> and
    /// the property <paramref name="value"/> when
    /// <see cref="Properties"/> contains <paramref name="name"/>.
    /// </summary>
    public bool TryGetProperty(
        string name,
        out string value)
    {
        if (this.Properties is { } properties && properties.TryGetValue(name, out var v))
        {
            value = v;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// The duration the content pipeline measured for a music entry (its
    /// <c>DurationSeconds</c> property), if it did. When it didn't, or the value doesn't parse, a
    /// platform loader falls back to the codec's.
    /// </summary>
    [PlatformContract]
    internal bool TryGetMusicDuration(
        out TimeSpan duration)
    {
        if (this.TryGetProperty("DurationSeconds", out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            && seconds >= 0.0)
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// The sample rate and channel count the content pipeline measured for a music entry (its
    /// <c>SampleRate</c> and <c>Channels</c> properties, both positive), if it did. With
    /// <see cref="TryGetMusicDuration"/>, a platform loader can create the decoder without
    /// opening the compressed stream at load time.
    /// </summary>
    [PlatformContract]
    internal bool TryGetMusicFormat(
        out int sampleRate,
        out int channels)
    {
        if (this.TryGetProperty("SampleRate", out var rawSampleRate)
            && int.TryParse(rawSampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out sampleRate)
            && sampleRate > 0
            && this.TryGetProperty("Channels", out var rawChannels)
            && int.TryParse(rawChannels, NumberStyles.Integer, CultureInfo.InvariantCulture, out channels)
            && channels > 0)
        {
            return true;
        }

        sampleRate = 0;
        channels = 0;
        return false;
    }
}
