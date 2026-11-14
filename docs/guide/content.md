# Loading content

Content is built by [the pipeline](content-pipeline.md) and loaded at run time through
[`Game.Content`](../api/Framework/ContentService.md).

## Content keys

You load content by its **key**: its path under `Content/`, with no extension. The file's format can
differ on each platform, and you never see it:

```csharp
protected override async Task OnStart()
{
    this.texture = await this.Content.LoadAsync<Texture>("Textures/bear");
    this.jump = await this.Content.LoadAsync<Sound>("Sound/jump");
}
```

Loading the same key again gives you the same object, without waiting for a second load. To load
several things faster, start them together:

```csharp
protected override async Task OnStart()
{
    var bear = this.Content.LoadAsync<Texture>("Textures/bear");
    var jump = this.Content.LoadAsync<Sound>("Sound/jump");
    await Task.WhenAll(bear, jump);

    this.texture = await bear;
}
```

To find something only if it's already loaded, without waiting:

```csharp
if (this.Content.TryGet<Texture>("Textures/bear", out var bear))
{
    this.texture = bear;
}
```

Content loads on background threads once the game has started, so loading doesn't hold up frames,
except on the web, which has only the one thread.

## Bundles

A [`ContentBundle`](../api/Framework/ContentBundle.md) is a named set of content that loads and
unloads together, such as a level's. Its items load in parallel:

```csharp
private async Task LoadForest()
{
    var bundle = this.Content.Bundles.Add("forest");
    bundle.Add<Texture>("Levels/forest/background");
    bundle.Add<Music>("Levels/forest/theme");

    await bundle.LoadAsync();
}

private async Task LeaveForest()
{
    await this.Content.Bundles["forest"].UnloadAsync();
}
```

## Who releases what

Everyone who loads a key shares one object, so content loaded by key belongs to the content service:

- Release it with `Content.UnloadAsync(key)`, or by unloading its bundle.
- A key that another loaded bundle uses stays until that bundle is unloaded too, and one you loaded
  directly stays until you unload it.
- `Dispose` on a texture loaded by key throws, and other loaded types don't offer it.

What you make yourself is yours to dispose: textures from `Graphics.CreateTexture`, and render
textures.

## Data files

Files you add as `DataContent` can be read as streams, by their file names:

```csharp
private async Task<List<string>> ReadNotes()
{
    var lines = new List<string>();
    using var stream = await this.Content.OpenStreamAsync("Data/notes.txt");
    using var reader = new StreamReader(stream);
    while (await reader.ReadLineAsync() is { } line)
    {
        lines.Add(line);
    }

    return lines;
}
```

`OpenStreamAsync` takes the file's path under `Content`, with its extension.

## Your own content types

A loader teaches the content service a new type. A type implements
[`IContent`](../api/Framework/IContent.md), and a loader implements
[`IContentLoader<T>`](../api/Framework/IContentLoader-1.md):

```csharp
internal sealed class LevelData : IContent
{
    public LevelData(string[] rows)
    {
        this.Rows = rows;
    }

    public string[] Rows { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        this.IsDisposed = true;
    }
}

internal sealed class LevelLoader : IContentLoader<LevelData>
{
    private readonly ContentService content;

    public LevelLoader(ContentService content)
    {
        this.content = content;
    }

    public async Task<LevelData> LoadAsync(string contentKey)
    {
        using var stream = await this.content.OpenStreamAsync(contentKey + ".txt").ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        return new LevelData(text.Split('\n'));
    }
}
```

Register it in `OnInitialize` or `OnStart`, and load it as usual:

```csharp
protected override async Task OnStart()
{
    this.Content.RegisterLoader(new LevelLoader(this.Content));
    var level = await this.Content.LoadAsync<LevelData>("Levels/first");
}
```

A loader can run on the game thread or a background thread, so don't assume either:

- Do parsing and decoding inline, without wrapping it in `Task.Run`. The content service has already
  chosen the thread.
- Do anything that needs the game thread, such as creating a texture, through
  [`GameThread.InvokeAsync`](../api/Framework/GameThread.md#invokeasync). If
  you're already on the game thread, it just runs it.
- Use `ConfigureAwait(false)` on awaited I/O, to avoid needless hops back to the game thread.

To build the files too, see [your own content items](content-pipeline.md#your-own-content-items).

## See also

- [`ContentService`](../api/Framework/ContentService.md)
- [The content pipeline](content-pipeline.md)
- The framework sample's Time and content page
