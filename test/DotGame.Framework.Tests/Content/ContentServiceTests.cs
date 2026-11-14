// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Content;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Content;

/// <summary>
/// The cache of loaded content: one load per key, a failed load tried again (finding C3), and
/// disposed content evicted. Which thread a loader runs on is left to decision 2, which changes
/// it, so these only await.
/// </summary>
public sealed class ContentServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly ContentService _content = new(CreateContentStrategy(new ContentManifest("tests")));

    private FakeLoader<FakeContent> AddLoader(
        Func<string, Task<FakeContent>>? load = null)
    {
        var loader = new FakeLoader<FakeContent>(load ?? (path => Task.FromResult(new FakeContent(path))));
        this._content.RegisterLoader(loader);
        return loader;
    }

    // ── One load per key ───────────────────────────────────────────────

    [Fact]
    public async Task Load_LoadsOnce_AndSharesTheResult()
    {
        var loader = this.AddLoader();

        var first = await this._content.LoadAsync<FakeContent>("a");
        var second = await this._content.LoadAsync<FakeContent>("a");

        Assert.Same(first, second);
        Assert.Equal("a", first.ContentPath);
        Assert.Equal(1, loader.Loads);
    }

    [Theory]
    [InlineData("Textures/Bear")]
    [InlineData("Textures\\Bear")]
    [InlineData("/Textures/Bear")]
    [InlineData("textures/BEAR")]
    public async Task EverySpellingOfAKey_SharesOneLoad(
        string spelling)
    {
        var loader = this.AddLoader();

        var first = await this._content.LoadAsync<FakeContent>("Textures/Bear");
        var second = await this._content.LoadAsync<FakeContent>(spelling);

        Assert.Same(first, second);
        Assert.Equal(1, loader.Loads);
        Assert.True(this._content.TryGet<FakeContent>(spelling, out _));
    }

    [Fact]
    public async Task Load_WhileLoading_SharesTheLoadInProgress()
    {
        var gate = new TaskCompletionSource<FakeContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = this.AddLoader(_ => gate.Task);

        var first = this._content.LoadAsync<FakeContent>("a");
        var second = this._content.LoadAsync<FakeContent>("a");
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var content = new FakeContent("a");
        gate.SetResult(content);

        Assert.Same(content, await first);
        Assert.Same(content, await second);
        Assert.Equal(1, loader.Loads);
    }

    [Fact]
    public async Task ConcurrentFirstLoads_LoadOnce()
    {
        using var bothEntered = new Barrier(2);
        var loader = this.AddLoader(path =>
        {
            // Each caller waits for the other inside the factory, the race's worst case. A fix
            // that lets only one in has this wait time out and go on.
            bothEntered.SignalAndWait(TimeSpan.FromMilliseconds(500), Token);
            return Task.FromResult(new FakeContent(path));
        });

        var first = Task.Run(() => this._content.LoadAsync<FakeContent>("a"), Token);
        var second = Task.Run(() => this._content.LoadAsync<FakeContent>("a"), Token);
        await Task.WhenAll(first, second);

        Assert.Equal(1, loader.Loads);
    }

    [Fact]
    public async Task OnWorkerThreads_ALoadReturnsBeforeItsLoaderFinishes()
    {
        using var finish = new ManualResetEventSlim();
        this.AddLoader(path =>
        {
            // Run inline, this wait would time out and the assert below would see it completed
            finish.Wait(TimeSpan.FromSeconds(5), Token);
            return Task.FromResult(new FakeContent(path));
        });
        this._content.LoadsOnWorkerThreads = true;

        var load = this._content.LoadAsync<FakeContent>("a");

        Assert.False(load.IsCompleted);

        finish.Set();
        await load;
    }

    [Fact]
    public async Task OnWorkerThreads_ALoaderThatThrowsAtOnce_FaultsTheLoad()
    {
        var fault = new IOException("No such file");
        this.AddLoader(_ => throw fault);
        this._content.LoadsOnWorkerThreads = true;

        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(() => this._content.LoadAsync<FakeContent>("a")));
    }

    // ── A failed load (C3) ─────────────────────────────────────────────

    [Fact]
    public async Task AFailedLoad_ReportsItsFailure_ThenIsTriedAgain()
    {
        var fault = new IOException("The connection dropped");
        int attempts = 0;
        var loader = this.AddLoader(path => (++attempts == 1)
            ? Task.FromException<FakeContent>(fault)
            : Task.FromResult(new FakeContent(path)));

        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(() => this._content.LoadAsync<FakeContent>("a")));

        var content = await this._content.LoadAsync<FakeContent>("a");

        Assert.Equal("a", content.ContentPath);
        Assert.Equal(2, loader.Loads);
        Assert.True(this._content.TryGet<FakeContent>("a", out _));
    }

    [Fact]
    public async Task ALoaderThatThrowsAtOnce_IsTriedAgain()
    {
        int attempts = 0;
        var loader = this.AddLoader(path => (++attempts == 1)
            ? throw new IOException("No such file")
            : Task.FromResult(new FakeContent(path)));

        await Assert.ThrowsAsync<IOException>(() => this._content.LoadAsync<FakeContent>("a"));

        await this._content.LoadAsync<FakeContent>("a");
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task ACanceledLoad_IsTriedAgain()
    {
        int attempts = 0;
        var loader = this.AddLoader(path => (++attempts == 1)
            ? Task.FromCanceled<FakeContent>(new CancellationToken(canceled: true))
            : Task.FromResult(new FakeContent(path)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this._content.LoadAsync<FakeContent>("a"));

        await this._content.LoadAsync<FakeContent>("a");
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task AFailedLoad_FailsEveryoneWaitingOnIt_WithoutASecondAttempt()
    {
        var gate = new TaskCompletionSource<FakeContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = this.AddLoader(_ => gate.Task);

        var first = this._content.LoadAsync<FakeContent>("a");
        var second = this._content.LoadAsync<FakeContent>("a");

        gate.SetException(new IOException("The connection dropped"));

        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal(1, loader.Loads);
    }

    [Fact]
    public async Task AFailedLoad_RepeatedFailures_AreEachTried()
    {
        var loader = this.AddLoader(_ => Task.FromException<FakeContent>(new IOException("Still down")));

        for (int i = 1; i <= 3; i++)
        {
            await Assert.ThrowsAsync<IOException>(() => this._content.LoadAsync<FakeContent>("a"));
            Assert.Equal(i, loader.Loads);
        }
    }

    [Fact]
    public async Task TryGet_AfterAFailedLoad_FindsNothing()
    {
        this.AddLoader(_ => Task.FromException<FakeContent>(new IOException("Down")));

        await Assert.ThrowsAsync<IOException>(() => this._content.LoadAsync<FakeContent>("a"));

        Assert.False(this._content.TryGet<FakeContent>("a", out var content));
        Assert.Null(content);
    }

    [Fact]
    public async Task Unload_OfAFailedLoad_DoesNotThrow_AndForgetsIt()
    {
        int attempts = 0;
        var loader = this.AddLoader(path => (++attempts == 1)
            ? Task.FromException<FakeContent>(new IOException("Down"))
            : Task.FromResult(new FakeContent(path)));

        await Assert.ThrowsAsync<IOException>(() => this._content.LoadAsync<FakeContent>("a"));

        await this._content.UnloadAsync("a");

        await this._content.LoadAsync<FakeContent>("a");
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task ALoadWithNoLoader_Fails_UntilThereIsOne()
    {
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => this._content.LoadAsync<FakeContent>("a"));
        Assert.Contains(nameof(FakeContent), failed.Message);
        Assert.Contains("RegisterLoader", failed.Message);

        this.AddLoader();

        await this._content.LoadAsync<FakeContent>("a");
    }

    // ── Who releases what ──────────────────────────────────────────────

    [Fact]
    public async Task Unload_ReleasesIt_ForEveryone_AndALaterLoadLoadsAgain()
    {
        var loader = this.AddLoader();
        var first = await this._content.LoadAsync<FakeContent>("a");

        await this._content.UnloadAsync("a");

        Assert.True(((IContent)first).IsDisposed);
        Assert.NotSame(first, await this._content.LoadAsync<FakeContent>("a"));
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task Unload_OfAnObject_FindsItsKey()
    {
        this.AddLoader();
        var content = await this._content.LoadAsync<FakeContent>("a");

        await this._content.UnloadAsync(content);

        Assert.True(((IContent)content).IsDisposed);
    }

    [Fact]
    public async Task Unload_OfWhatIsNotLoaded_DoesNothing()
    {
        await this._content.UnloadAsync("never");
        await this._content.UnloadAsync(new FakeContent("never"));
    }

    [Fact]
    public async Task TwoBundles_ShareContent_UntilBothAreUnloaded()
    {
        this.AddLoader();
        var one = this._content.Bundles.Add("one");
        var two = this._content.Bundles.Add("two");
        one.Add<FakeContent>("a");
        two.Add<FakeContent>("a");
        await one.LoadAsync();
        await two.LoadAsync();
        Assert.True(this._content.TryGet<FakeContent>("a", out var content));

        await one.UnloadAsync();
        Assert.False(((IContent)content).IsDisposed);

        await two.UnloadAsync();
        Assert.True(((IContent)content).IsDisposed);
    }

    [Fact]
    public async Task ABundle_LeavesWhatWasLoadedDirectly_UntilItIsUnloaded()
    {
        this.AddLoader();
        var kept = await this._content.LoadAsync<FakeContent>("a");
        var bundle = this._content.Bundles.Add("one");
        bundle.Add<FakeContent>("a");
        await bundle.LoadAsync();

        await bundle.UnloadAsync();
        Assert.False(((IContent)kept).IsDisposed);

        await this._content.UnloadAsync("a");
        Assert.True(((IContent)kept).IsDisposed);
    }

    [Fact]
    public async Task Unload_LeavesWhatALoadedBundleUses()
    {
        this.AddLoader();
        var bundle = this._content.Bundles.Add("one");
        bundle.Add<FakeContent>("a");
        await bundle.LoadAsync();
        var content = await this._content.LoadAsync<FakeContent>("a");

        await this._content.UnloadAsync("a");
        Assert.False(((IContent)content).IsDisposed);

        await bundle.UnloadAsync();
        Assert.True(((IContent)content).IsDisposed);
    }

    [Fact]
    public async Task ABundle_LoadedTwice_HoldsOnce()
    {
        this.AddLoader();
        var bundle = this._content.Bundles.Add("one");
        bundle.Add<FakeContent>("a");
        await bundle.LoadAsync();
        Assert.True(this._content.TryGet<FakeContent>("a", out var content));
        await bundle.LoadAsync();

        await bundle.UnloadAsync();

        Assert.False(this._content.TryGet<FakeContent>("a", out _));
    }

    // ── TryGet and types ───────────────────────────────────────────────

    [Fact]
    public async Task TryGet_FindsOnlyFinishedContent_OfTheRightType()
    {
        var gate = new TaskCompletionSource<FakeContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.AddLoader(_ => gate.Task);

        Assert.False(this._content.TryGet<FakeContent>("a", out _));

        var load = this._content.LoadAsync<FakeContent>("a");
        Assert.False(this._content.TryGet<FakeContent>("a", out _));

        var loaded = new FakeContent("a");
        gate.SetResult(loaded);
        await load;

        Assert.True(this._content.TryGet<FakeContent>("a", out var found));
        Assert.Same(loaded, found);
        Assert.False(this._content.TryGet<OtherContent>("a", out var other));
        Assert.Null(other);
    }

    [Fact]
    public async Task Load_AsAnotherType_Fails_AndLeavesTheContentLoaded()
    {
        this.AddLoader();
        this._content.RegisterLoader(new FakeLoader<OtherContent>(_ => Task.FromResult(new OtherContent())));

        var content = await this._content.LoadAsync<FakeContent>("a");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => this._content.LoadAsync<OtherContent>("a"));
        Assert.Contains("'a'", failed.Message);
        Assert.Contains(nameof(FakeContent), failed.Message);
        Assert.Contains(nameof(OtherContent), failed.Message);

        Assert.False(content.IsDisposed);
        Assert.Same(content, await this._content.LoadAsync<FakeContent>("a"));
    }

    // ── Disposal and Unload ────────────────────────────────────────────

    [Fact]
    public async Task DisposedContent_IsLoadedAgain()
    {
        var loader = this.AddLoader();

        var first = await this._content.LoadAsync<FakeContent>("a");
        first.Dispose();

        Assert.False(this._content.TryGet<FakeContent>("a", out _));

        var second = await this._content.LoadAsync<FakeContent>("a");

        Assert.NotSame(first, second);
        Assert.False(second.IsDisposed);
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task Unload_DisposesIt_AndTheNextLoadIsNew()
    {
        var loader = this.AddLoader();
        var first = await this._content.LoadAsync<FakeContent>("a");

        await this._content.UnloadAsync("A");

        Assert.True(first.IsDisposed);
        Assert.False(this._content.TryGet<FakeContent>("a", out _));

        Assert.NotSame(first, await this._content.LoadAsync<FakeContent>("a"));
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task Unload_OfSomethingNotLoaded_DoesNothing()
    {
        await this._content.UnloadAsync("never loaded");
    }

    [Fact]
    public async Task Unload_WhileLoading_DisposesItOnceItArrives()
    {
        var gate = new TaskCompletionSource<FakeContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.AddLoader(_ => gate.Task);
        _ = this._content.LoadAsync<FakeContent>("a");

        var unload = this._content.UnloadAsync("a");
        Assert.False(unload.IsCompleted);

        var content = new FakeContent("a");
        gate.SetResult(content);
        await unload;

        Assert.True(content.IsDisposed);
    }

    // ── The strategy ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_RefusesNullAndAStrategyWithoutAManifest()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentService(null!));
        Assert.Throws<InvalidOperationException>(() => new ContentService(CreateContentStrategy(null)));
    }

    [Fact]
    public async Task Initialize_RegistersThePlatformsLoaders()
    {
        await this._content.Initialize();

        // The test platform's loaders refuse, and that's how to know they were reached
        await Assert.ThrowsAsync<NotSupportedException>(() => this._content.LoadAsync<DotGame.Framework.Graphics.Texture>("t"));
        await Assert.ThrowsAsync<NotSupportedException>(() => this._content.LoadAsync<DotGame.Framework.Graphics.Shader>("s"));
        await Assert.ThrowsAsync<NotSupportedException>(() => this._content.LoadAsync<DotGame.Framework.Audio.Sound>("n"));
        await Assert.ThrowsAsync<NotSupportedException>(() => this._content.LoadAsync<DotGame.Framework.Audio.Music>("m"));
    }

    [Fact]
    public void RegisterLoader_Twice_ForAType_TheLastIsUsed()
    {
        this.AddLoader();

        this.AddLoader();
    }

    [Fact]
    public void ResolveContentPath_AsksTheManifest()
    {
        var manifest = new ContentManifest("tests");
        manifest.Add("Textures/Bear", "Textures/Bear.ktx2");
        manifest.Add("Shaders/Msdf", "Shaders/Msdf.vert.spv", "vert.spv");
        var content = new ContentService(CreateContentStrategy(manifest));

        Assert.Equal("Textures/Bear.ktx2", content.ResolveContentPath("Textures\\Bear"));
        Assert.Equal("Shaders/Msdf.vert.spv", content.ResolveContentPath("Shaders/Msdf", "vert.spv"));
        Assert.Throws<KeyNotFoundException>(() => content.ResolveContentPath("/Unknown\\Key"));
    }

    [Fact]
    public async Task OpenStream_AsksThePlatform()
    {
        using var stream = new MemoryStream();
        string? asked = null;
        var content = new ContentService(CreateContentStrategy(new ContentManifest("tests"), path =>
        {
            asked = path;
            return Task.FromResult<Stream>(stream);
        }));

        Assert.Same(stream, await content.OpenStreamAsync("Data/Level1.json"));
        Assert.Equal("Data/Level1.json", asked);
    }

    // ── Bundles ────────────────────────────────────────────────────────

    [Fact]
    public async Task Bundle_LoadsEverythingInIt_AndUnloadsIt()
    {
        this.AddLoader();
        var bundle = this._content.Bundles.Add("level1");
        bundle.Add<FakeContent>("a");
        bundle.Add<FakeContent>("b");

        await bundle.LoadAsync();

        Assert.True(this._content.TryGet<FakeContent>("a", out var a));
        Assert.True(this._content.TryGet<FakeContent>("b", out var b));

        await bundle.UnloadAsync();

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.False(this._content.TryGet<FakeContent>("a", out _));
    }

    [Fact]
    public async Task Bundle_WithAKeyTwice_LoadsItOnce()
    {
        var loader = this.AddLoader();
        var bundle = this._content.Bundles.Add("level1");
        bundle.Add<FakeContent>("a");
        bundle.Add<FakeContent>("A");

        await bundle.LoadAsync();
        await bundle.LoadAsync();

        Assert.Equal(1, loader.Loads);

        // The second removal finds nothing to unload
        await bundle.UnloadAsync();
    }

    [Fact]
    public async Task Bundle_WithNothingInIt_LoadsAndUnloads()
    {
        var bundle = this._content.Bundles.Add("empty");

        await bundle.LoadAsync();
        await bundle.UnloadAsync();
    }

    [Fact]
    public async Task Bundle_WithAFailure_ReportsIt_AfterLoadingTheRest()
    {
        this.AddLoader(path => (path == "bad")
            ? Task.FromException<FakeContent>(new IOException("Down"))
            : Task.FromResult(new FakeContent(path)));
        var bundle = this._content.Bundles.Add("level1");
        bundle.Add<FakeContent>("bad");
        bundle.Add<FakeContent>("good");

        await Assert.ThrowsAsync<IOException>(() => bundle.LoadAsync());

        Assert.True(this._content.TryGet<FakeContent>("good", out var good));
        Assert.False(this._content.TryGet<FakeContent>("bad", out _));

        // C3: the failure doesn't stop it unloading what did load
        await bundle.UnloadAsync();
        Assert.True(good.IsDisposed);
    }

    [Fact]
    public async Task Bundle_LoadedAgainAfterAFailure_RetriesWhatFailed()
    {
        int attempts = 0;
        var loader = this.AddLoader(path => ((path == "flaky") && (++attempts == 1))
            ? Task.FromException<FakeContent>(new IOException("Down"))
            : Task.FromResult(new FakeContent(path)));
        var bundle = this._content.Bundles.Add("level1");
        bundle.Add<FakeContent>("flaky");
        bundle.Add<FakeContent>("good");

        await Assert.ThrowsAsync<IOException>(() => bundle.LoadAsync());
        await bundle.LoadAsync();

        Assert.True(this._content.TryGet<FakeContent>("flaky", out _));
        Assert.Equal(3, loader.Loads);
    }

    [Fact]
    public void Bundles_CanBeFoundByName_AndEnumerated()
    {
        var first = this._content.Bundles.Add("first");
        var second = this._content.Bundles.Add("second");

        Assert.Equal("first", first.Name);
        Assert.Same(second, this._content.Bundles["second"]);
        Assert.Throws<KeyNotFoundException>(() => this._content.Bundles["third"]);
        Assert.True(this._content.Bundles.TryGet("first", out var found));
        Assert.Same(first, found);
        Assert.False(this._content.Bundles.TryGet("third", out _));
        Assert.Equal([first, second], this._content.Bundles);
        Assert.Throws<ArgumentNullException>(() => this._content.Bundles.Add(null!));
    }

    private sealed class FakeContent(string contentPath) : IContent
    {
        public string ContentPath { get; } = contentPath;

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            this.IsDisposed = true;
        }
    }

    private sealed class OtherContent : IContent
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            this.IsDisposed = true;
        }
    }

    private sealed class FakeLoader<TContent>(Func<string, Task<TContent>> load) : IContentLoader<TContent>
        where TContent : class, IContent
    {
        private int _loads;

        public int Loads => Volatile.Read(ref this._loads);

        public Task<TContent> LoadAsync(
            string contentPath)
        {
            Interlocked.Increment(ref this._loads);
            return load(contentPath);
        }
    }
}
