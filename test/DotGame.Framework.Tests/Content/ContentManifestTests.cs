// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Globalization;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Content;
using Xunit;

namespace DotGame.Framework.Tests.Content;

/// <summary>
/// The map from a content key to its built file, its layering rules, and the process-wide
/// registry snapshotted into it at startup.
/// </summary>
public sealed class ContentManifestTests
{
    // ── Keys ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Textures/Bear")]
    [InlineData("Textures\\Bear")]
    [InlineData("/Textures/Bear")]
    [InlineData("\\Textures\\Bear")]
    [InlineData("TEXTURES/bear")]
    public void EverySpellingOfAKey_FindsTheEntry(
        string spelling)
    {
        var manifest = new ContentManifest("tests");
        manifest.Add("Textures/Bear", "Textures/Bear.ktx2");

        Assert.True(manifest.TryGet(spelling, out var entry));
        Assert.Equal("Textures/Bear.ktx2", entry.Path);
        Assert.Equal("Textures/Bear.ktx2", manifest[spelling].Path);
        Assert.Equal("Textures/Bear.ktx2", manifest.ResolveContentPath(spelling));
    }

    [Fact]
    public void Add_NormalizesKeyAndPath_KeepingTheirCase()
    {
        var manifest = new ContentManifest("tests");

        manifest.Add("\\Textures\\Bear", "/Out\\Bear.KTX2");
        Assert.True(manifest.TryGet("Textures/Bear", out var entry));

        Assert.Equal("Textures/Bear", entry.Key);
        Assert.Equal("Out/Bear.KTX2", entry.Path);
        Assert.Null(entry.SubKey);
        Assert.Null(entry.Properties);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("Shaders/Msdf:vert.spv")]
    public void Add_RefusesAKeyThatLooksLikeItHasASubKey(
        string key)
    {
        var manifest = new ContentManifest("tests");

        var refused = Assert.Throws<ArgumentException>(() => manifest.Add(key, "path"));

        Assert.Equal("key", refused.ParamName);
        Assert.Equal(0, manifest.Count);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Add_RefusesASubKeyWithASeparator(
        string subKey)
    {
        var manifest = new ContentManifest("tests");

        var refused = Assert.Throws<ArgumentException>(() => manifest.Add("key", "path", subKey));

        Assert.Equal("subKey", refused.ParamName);
    }

    [Fact]
    public void Add_RefusesAnEmptyKeyOrPath()
    {
        var manifest = new ContentManifest("tests");

        Assert.Throws<ArgumentException>(() => manifest.Add(string.Empty, "path"));
        Assert.Throws<ArgumentException>(() => manifest.Add("key", string.Empty));
        Assert.Throws<ArgumentNullException>(() => manifest.Add(null!, "path"));
        Assert.Throws<ArgumentNullException>(() => manifest.Add("key", null!));
    }

    [Fact]
    public void Add_TheLastRegistrationWins()
    {
        var manifest = new ContentManifest("tests");

        manifest.Add("key", "first");
        manifest.Add("KEY", "second");

        Assert.Equal(1, manifest.Count);
        Assert.Equal("second", manifest.ResolveContentPath("key"));
    }

    [Fact]
    public void SubKeys_AreSeparateEntries_AndAnEmptyOneIsNone()
    {
        var manifest = new ContentManifest("tests");

        manifest.Add("Shaders/Msdf", "msdf.bin");
        manifest.Add("Shaders/Msdf", "msdf.vert.spv", "vert.spv");
        manifest.Add("Shaders/Msdf", "msdf.frag.spv", "FRAG.spv");

        Assert.Equal(3, manifest.Count);
        Assert.Equal("msdf.bin", manifest.ResolveContentPath("Shaders/Msdf"));
        Assert.Equal("msdf.bin", manifest.ResolveContentPath("Shaders/Msdf", string.Empty));
        Assert.Equal("msdf.vert.spv", manifest.ResolveContentPath("Shaders/Msdf", "vert.spv"));
        Assert.Equal("msdf.frag.spv", manifest.ResolveContentPath("shaders/msdf", "frag.SPV"));
        Assert.Equal("vert.spv", manifest["Shaders/Msdf", "vert.spv"].SubKey);
    }

    [Fact]
    public void ResolveContentPath_WithNoEntry_Throws_NamingTheKey()
    {
        // No falling back to the key as a path: a mistaken key is reported as one, the same on
        // every platform
        var manifest = new ContentManifest("tests");
        manifest.Add("Textures/Bear", "Textures/Bear.ktx2");

        var error = Assert.Throws<KeyNotFoundException>(() => manifest.ResolveContentPath("\\Textures\\Bear.png"));
        Assert.Contains("'\\Textures\\Bear.png'", error.Message, StringComparison.Ordinal);
        error = Assert.Throws<KeyNotFoundException>(() => manifest.ResolveContentPath("Textures/Bear", "missing"));
        Assert.Contains("sub-key 'missing'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexer_ThrowsForAMissingKey_NamingIt()
    {
        var manifest = new ContentManifest("tests");

        var missing = Assert.Throws<KeyNotFoundException>(() => manifest["Missing/Key", "sub"]);

        Assert.Contains("Missing/Key:sub", missing.Message);
    }

    [Fact]
    public void Lookups_RefuseANullKey()
    {
        var manifest = new ContentManifest("tests");

        Assert.Throws<ArgumentNullException>(() => manifest.TryGet(null!, out _));
        Assert.Throws<ArgumentNullException>(() => manifest[null!]);
        Assert.Throws<ArgumentNullException>(() => manifest.ResolveContentPath(null!));
    }

    [Fact]
    public void Remove_TakesOutOneEntry()
    {
        var manifest = new ContentManifest("tests");
        manifest.Add("key", "path");
        manifest.Add("key", "sub path", "sub");

        Assert.False(manifest.Remove("missing"));
        Assert.True(manifest.Remove("KEY", "SUB"));
        Assert.False(manifest.TryGet("key", out _, "sub"));
        Assert.True(manifest.TryGet("key", out _));
        Assert.True(manifest.Remove("\\key"));
        Assert.Equal(0, manifest.Count);
    }

    [Fact]
    public void Enumerating_SeesASnapshot_SoAddingDuringItIsSafe()
    {
        var manifest = new ContentManifest("tests");
        manifest.Add("a", "1");
        manifest.Add("b", "2");

        int seen = 0;
        foreach (var entry in manifest.GetEntries())
        {
            seen++;
            manifest.Add(entry.Key + "/copy", entry.Path);
        }

        Assert.Equal(2, seen);
        Assert.Equal(4, manifest.Count);
    }

    // ── The manifest itself ────────────────────────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("Content", "Content/")]
    [InlineData("Content/", "Content/")]
    [InlineData("/Content/", "Content/")]
    [InlineData("Game\\Content", "Game/Content/")]
    public void BasePath_IsNormalized(
        string? basePath,
        string expected)
    {
        Assert.Equal(expected, new ContentManifest("tests", basePath).BasePath);
    }

    [Fact]
    public void Constructor_RefusesAnEmptyIdentity()
    {
        Assert.Throws<ArgumentException>(() => new ContentManifest(string.Empty));
        Assert.Throws<ArgumentNullException>(() => new ContentManifest(null!));
    }

    // ── Layering ───────────────────────────────────────────────────────

    [Fact]
    public void AddManifest_FoldsItsEntriesIn_WithSubKeysAndProperties()
    {
        var mod = new ContentManifest("mod");
        mod.Add("a", "a.bin");
        mod.Add("b", "b.bin", "sub", [new("Name", "Value")]);

        var merged = new ContentManifest("merged");
        merged.Add(mod);

        Assert.Equal(2, merged.Count);
        Assert.Equal("a.bin", merged.ResolveContentPath("a"));
        Assert.True(merged["b", "sub"].TryGetProperty("Name", out var value));
        Assert.Equal("Value", value);
    }

    [Fact]
    public void AddManifest_WithTheSamePath_IsAllowed()
    {
        var mod = new ContentManifest("mod");
        mod.Add("a", "A.bin");

        var merged = new ContentManifest("merged");
        merged.Add("a", "a.BIN");
        merged.Add(mod);
        merged.Add(mod);

        Assert.Equal(1, merged.Count);
        Assert.Equal("a.BIN", merged.ResolveContentPath("a"));
    }

    [Fact]
    public void AddManifest_WithADifferentPath_IsAConflict_NamingBoth()
    {
        var mod = new ContentManifest("AcmeMods.WinterPack");
        mod.Add("a", "winter.bin");

        var merged = new ContentManifest("merged");
        merged.Add("a", "base.bin");

        var conflict = Assert.Throws<InvalidOperationException>(() => merged.Add(mod));

        Assert.Contains("AcmeMods.WinterPack", conflict.Message);
        Assert.Contains("base.bin", conflict.Message);
        Assert.Contains("winter.bin", conflict.Message);
        Assert.Equal("base.bin", merged.ResolveContentPath("a"));
    }

    [Fact]
    public void RemoveManifest_TakesOutWhatItContributed()
    {
        var mod = new ContentManifest("mod");
        mod.Add("a", "a.bin");
        mod.Add("b", "b.bin");
        mod.Add("not merged", "c.bin");

        var merged = new ContentManifest("merged");
        merged.Add("base", "base.bin");
        merged.Add("a", "a.bin");
        merged.Add("b", "B.BIN");

        Assert.Equal(2, merged.Remove(mod));

        Assert.Equal(1, merged.Count);
        Assert.True(merged.TryGet("base", out _));
    }

    [Fact]
    public void RemoveManifest_WhenSomethingLayeredOver_RefusesToRemoveIt()
    {
        var mod = new ContentManifest("mod");
        mod.Add("a", "a.bin");

        var merged = new ContentManifest("merged");
        merged.Add("a", "another.bin");

        var refused = Assert.Throws<InvalidOperationException>(() => merged.Remove(mod));

        Assert.Contains("mod", refused.Message);
        Assert.Equal("another.bin", merged.ResolveContentPath("a"));
    }

    [Fact]
    public void ManifestArguments_MustNotBeNull()
    {
        var manifest = new ContentManifest("tests");

        Assert.Throws<ArgumentNullException>(() => manifest.Add((ContentManifest)null!));
        Assert.Throws<ArgumentNullException>(() => manifest.Remove((ContentManifest)null!));
    }

    // ── Properties ─────────────────────────────────────────────────────

    public static TheoryData<bool> PassedAsArray => [true, false];

    [Theory]
    [MemberData(nameof(PassedAsArray))]
    public void Properties_KeepTheValidOnes_TheLastOfAName(
        bool asArray)
    {
        KeyValuePair<string, string>[] properties =
        [
            new("Keep", "1"),
            new(string.Empty, "no name"),
            new("No value", null!),
            new("keep", "different case"),
            new("Keep", "2"),
        ];

        var manifest = new ContentManifest("tests");
        manifest.Add("key", "path", properties: asArray ? properties : new List<KeyValuePair<string, string>>(properties));
        Assert.True(manifest.TryGet("key", out var entry));

        Assert.Equal(2, entry.Properties!.Count);
        Assert.True(entry.TryGetProperty("Keep", out var value));
        Assert.Equal("2", value);
        Assert.True(entry.TryGetProperty("keep", out value));
        Assert.Equal("different case", value);
    }

    [Fact]
    public void Properties_WithNothingValid_AreNull()
    {
        var manifest = new ContentManifest("tests");
        manifest.Add("key", "path", properties: []);
        Assert.True(manifest.TryGet("key", out var entry));

        Assert.Null(entry.Properties);
        Assert.False(entry.TryGetProperty("Anything", out var value));
        Assert.Empty(value);
    }

    [Fact]
    public void Register_MakesAPipelinesProperties_Readable_TheLastRegisteredWinning()
    {
        var key = "pipeline-" + Guid.NewGuid().ToString("N");
        var first = new ContentManifest(key + "-first");
        first.Add(key, "first", properties: [new KeyValuePair<string, string>("Format", "dds")]);
        var second = new ContentManifest(key + "-second");
        second.Add(key, "second", properties: [new KeyValuePair<string, string>("Format", "astc")]);

        Assert.False(ContentManifest.TryGetProperty(key, "Format", out _));

        ContentManifest.Register(first);
        Assert.True(ContentManifest.TryGetProperty(key, "Format", out var format));
        Assert.Equal("dds", format);

        ContentManifest.Register(second);
        Assert.True(ContentManifest.TryGetProperty(key, "Format", out format));
        Assert.Equal("astc", format);
        Assert.False(ContentManifest.TryGetProperty(key, "Other", out _));
    }

    private static ContentManifestEntry Music(
        KeyValuePair<string, string>[] properties)
    {
        var manifest = new ContentManifest("tests");
        manifest.Add("music", "music.ogg", properties: properties);
        Assert.True(manifest.TryGet("music", out var entry));
        return entry;
    }

    [Theory]
    [InlineData("92.5", 92.5)]
    [InlineData("0", 0.0)]
    [InlineData("1e2", 100.0)]
    public void TryGetMusicDuration_ReadsTheInvariantProperty(
        string raw,
        double seconds)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.True(Music([new("DurationSeconds", raw)]).TryGetMusicDuration(out var duration));
            Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryGetMusicDuration_RefusesAnUnusableValue(
        string raw)
    {
        Assert.False(Music([new("DurationSeconds", raw)]).TryGetMusicDuration(out var duration));
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void TryGetMusicDuration_WithoutTheProperty_IsFalse()
    {
        Assert.False(Music([]).TryGetMusicDuration(out _));
    }

    [Fact]
    public void TryGetMusicFormat_ReadsBothProperties()
    {
        var entry = Music([new("SampleRate", "48000"), new("Channels", "2")]);

        Assert.True(entry.TryGetMusicFormat(out int sampleRate, out int channels));
        Assert.Equal(48000, sampleRate);
        Assert.Equal(2, channels);
    }

    [Theory]
    [InlineData("48000", null)]
    [InlineData(null, "2")]
    [InlineData("0", "2")]
    [InlineData("48000", "0")]
    [InlineData("-48000", "2")]
    [InlineData("forty", "2")]
    [InlineData("48000", "two")]
    [InlineData("48000.5", "2")]
    public void TryGetMusicFormat_NeedsBothToBePositive(
        string? sampleRate,
        string? channels)
    {
        var properties = new List<KeyValuePair<string, string>>();
        if (sampleRate is not null)
        {
            properties.Add(new("SampleRate", sampleRate));
        }

        if (channels is not null)
        {
            properties.Add(new("Channels", channels));
        }

        Assert.False(Music([.. properties]).TryGetMusicFormat(out int readRate, out int readChannels));
        Assert.Equal(0, readRate);
        Assert.Equal(0, readChannels);
    }

    // ── The registry ───────────────────────────────────────────────────

    [Fact]
    public void Registry_IsProcessWide()
    {
        Assert.Same(ContentManifest.Registry, ContentManifest.Registry);
    }

    [Fact]
    public void Register_ThenFind_AndUnregister()
    {
        var registry = new ContentManifestRegistry();
        var manifest = new ContentManifest("one");

        registry.Register(manifest);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet("one", out var found));
        Assert.Same(manifest, found);
        Assert.False(registry.TryGet("ONE", out _));

        Assert.True(registry.Unregister("one"));
        Assert.False(registry.Unregister("one"));
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGet("one", out _));
    }

    [Fact]
    public void Register_ASecondManifestWithAnIdentity_Fails_Until_TheFirstIsGone()
    {
        var registry = new ContentManifestRegistry();
        registry.Register(new ContentManifest("one"));

        var second = new ContentManifest("one");
        Assert.Throws<InvalidOperationException>(() => registry.Register(second));
        Assert.Equal(1, registry.Count);

        registry.Unregister("one");
        registry.Register(second);
        Assert.True(registry.TryGet("one", out var found));
        Assert.Same(second, found);
    }

    [Fact]
    public void Registry_EnumeratesInRegistrationOrder()
    {
        var registry = new ContentManifestRegistry();
        var c = new ContentManifest("c");
        var a = new ContentManifest("a");
        var b = new ContentManifest("b");

        registry.Register(c);
        registry.Register(a);
        registry.Register(b);
        registry.Unregister("a");
        registry.Register(a);

        Assert.Equal([c, b, a], registry);
    }

    [Fact]
    public void Registry_ChecksItsArguments()
    {
        var registry = new ContentManifestRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.Throws<ArgumentException>(() => registry.Unregister(string.Empty));
        Assert.Throws<ArgumentException>(() => registry.TryGet(string.Empty, out _));
        Assert.Throws<ArgumentException>(() => registry.BuildSnapshot(string.Empty));
    }

    [Fact]
    public void BuildSnapshot_MergesTheManifests_AndTheirBasePath()
    {
        var registry = new ContentManifestRegistry();
        var framework = new ContentManifest("framework", "Content/");
        framework.Add("a", "a.bin");
        var mod = new ContentManifest("mod");
        mod.Add("b", "b.bin");
        var library = new ContentManifest("library", "content");
        library.Add("c", "c.bin");

        registry.Register(mod);
        registry.Register(framework);
        registry.Register(library);

        var snapshot = registry.BuildSnapshot("game");

        Assert.Equal("game", snapshot.Identity);
        Assert.Equal("Content/", snapshot.BasePath);
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void BuildSnapshot_LeavesOutWhatIsAboutContent_ButNotAGameThatStartsTheSameWay()
    {
        var registry = new ContentManifestRegistry();
        foreach (string identity in new[] { "DotGame.LaunchScreen", "DotGame.ContentFingerprints", "DotGame.Samples.MyGame" })
        {
            var manifest = new ContentManifest(identity);
            manifest.Add(identity + "/key", "path");
            registry.Register(manifest);
        }

        var snapshot = registry.BuildSnapshot();

        Assert.True(snapshot.TryGet("DotGame.Samples.MyGame/key", out _));
        Assert.False(snapshot.TryGet("DotGame.LaunchScreen/key", out _));
        Assert.False(snapshot.TryGet("DotGame.ContentFingerprints/key", out _));
    }

    [Fact]
    public void BuildSnapshot_OfNothing_IsEmpty()
    {
        var snapshot = new ContentManifestRegistry().BuildSnapshot();

        Assert.Equal("default", snapshot.Identity);
        Assert.Empty(snapshot.BasePath);
        Assert.Equal(0, snapshot.Count);
    }

    [Fact]
    public void BuildSnapshot_WithDifferentBasePaths_Fails_NamingThem()
    {
        var registry = new ContentManifestRegistry();
        registry.Register(new ContentManifest("one", "Content/"));
        registry.Register(new ContentManifest("two", "Assets/"));

        var conflict = Assert.Throws<InvalidOperationException>(() => registry.BuildSnapshot());

        Assert.Contains("Content/", conflict.Message);
        Assert.Contains("Assets/", conflict.Message);
    }

    [Fact]
    public void BuildSnapshot_WithAConflictingKey_Fails()
    {
        var registry = new ContentManifestRegistry();
        var one = new ContentManifest("one");
        one.Add("a", "one.bin");
        var two = new ContentManifest("two");
        two.Add("a", "two.bin");
        registry.Register(one);
        registry.Register(two);

        Assert.Throws<InvalidOperationException>(() => registry.BuildSnapshot());
    }

    [Fact]
    public void ASnapshot_IsIndependentOfLaterChanges()
    {
        var registry = new ContentManifestRegistry();
        var manifest = new ContentManifest("one");
        manifest.Add("a", "a.bin");
        registry.Register(manifest);

        var snapshot = registry.BuildSnapshot();

        manifest.Add("b", "b.bin");
        manifest.Add("a", "changed.bin");
        registry.Unregister("one");

        Assert.Equal(1, snapshot.Count);
        Assert.Equal("a.bin", snapshot.ResolveContentPath("a"));
    }
}
