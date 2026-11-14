// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Graphics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;
using Xunit;

namespace DotGame.Framework.Tests.Graphics;

public sealed class TextureSetDataTests : IDisposable
{
    private sealed class Recording : ITextureStrategy
    {
        public Vector2Int Size => new(4, 2);

        public RectangleInt? Region { get; private set; }

        public int Pixels { get; private set; }

        public void SetData(
            RectangleInt region,
            ReadOnlySpan<Color32> data)
        {
            this.Region = region;
            this.Pixels = data.Length;
        }

        public void Dispose()
        {
        }
    }

    private readonly Recording _platform = new();

    private Texture NewTexture() => new(new Strategy<ITextureStrategy>(this._platform));

    public void Dispose()
    {
        this._platform.Dispose();
    }

    [Fact]
    public void No_region_means_the_whole_texture()
    {
        this.NewTexture().SetData(new Color32[8]);

        Assert.Equal(new RectangleInt(0, 0, 4, 2), this._platform.Region);
        Assert.Equal(8, this._platform.Pixels);
    }

    [Fact]
    public void A_region_reaches_the_platform()
    {
        this.NewTexture().SetData(new Color32[2], new RectangleInt(3, 0, 1, 2));

        Assert.Equal(new RectangleInt(3, 0, 1, 2), this._platform.Region);
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(4, 0, 1, 1)]
    [InlineData(0, 2, 1, 1)]
    [InlineData(0, 0, 5, 1)]
    [InlineData(0, 0, 1, 3)]
    [InlineData(0, 0, -1, 1)]
    public void A_region_outside_the_texture_is_refused(
        int x,
        int y,
        int width,
        int height)
    {
        var texture = this.NewTexture();
        var region = new RectangleInt(x, y, width, height);

        Assert.Throws<ArgumentOutOfRangeException>(() => texture.SetData(new Color32[Math.Max(width * height, 0)], region));
        Assert.Null(this._platform.Region);
    }

    [Fact]
    public void The_wrong_amount_of_data_is_refused()
    {
        var texture = this.NewTexture();

        Assert.Throws<ArgumentException>(() => texture.SetData(new Color32[7]));
    }

    [Fact]
    public void A_loaded_texture_is_refused()
    {
        var texture = this.NewTexture();
        texture.IsOwnedByContent = true;

        Assert.Throws<InvalidOperationException>(() => texture.SetData(new Color32[8]));
    }

    [Fact]
    public void An_empty_region_does_nothing()
    {
        this.NewTexture().SetData([], new RectangleInt(1, 1, 0, 0));

        Assert.Null(this._platform.Region);
    }
}
