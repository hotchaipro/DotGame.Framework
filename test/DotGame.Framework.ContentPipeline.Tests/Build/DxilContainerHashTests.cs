// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// The DXIL container hash, against dxc's own DxilHash.cpp.
/// </summary>
public sealed class DxilContainerHashTests
{
    // A container of the given size: bytes (i * 7 + 3) & 255, then the DXBC magic, a zero hash and the size
    private static byte[] Container(int size)
    {
        var container = new byte[size];
        for (int i = 0; i < size; i++)
        {
            container[i] = (byte)((i * 7) + 3);
        }

        "DXBC"u8.CopyTo(container);
        container.AsSpan(4, 16).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(24), (uint)size);
        return container;
    }

    // The hashes were made by dxc's DxilHash.cpp (ComputeHashRetail, built unchanged) signing the same
    // containers. They cover both of its endings: the bit counts in the last block of data (12, 76 and 980
    // bytes hashed, and 128, a whole number of blocks), and in a block of their own (60, 63 and 120).
    [Theory]
    [InlineData(32, "f5d8c1a64ee509b20c95d89f0fb3bb9f")]
    [InlineData(80, "4e1cbdc1d4791e6b2f5b1d6df05aeaa6")]
    [InlineData(83, "aa13f443aa2368f3075785638560f560")]
    [InlineData(96, "4bd307b1a43851d5511d48e42b660d03")]
    [InlineData(140, "0a6c1350ebaaee84055e289fc7eace75")]
    [InlineData(148, "58cd239b4daaf964c8cdbd40a1acb711")]
    [InlineData(1000, "2ff0ac55177e284a7e65a63c2702bd96")]
    public void Sign_WritesTheHashDxcWrites(int size, string expected)
    {
        byte[] container = Container(size);

        DxilContainerHash.Sign(container, "a.dxil");

        Assert.Equal(expected, Convert.ToHexStringLower(container.AsSpan(4, 16)));
    }

    [Fact]
    public void Sign_ChangesOnlyTheHash()
    {
        byte[] container = Container(200);
        byte[] original = (byte[])container.Clone();

        DxilContainerHash.Sign(container, "a.dxil");

        Assert.Equal(original.AsSpan(0, 4).ToArray(), container.AsSpan(0, 4).ToArray());
        Assert.Equal(original.AsSpan(20).ToArray(), container.AsSpan(20).ToArray());
    }

    [Fact]
    public void Sign_OfAFile_RewritesIt()
    {
        using var directory = new TestDirectory();
        string file = directory.Path_("a.dxil");
        File.WriteAllBytes(file, Container(32));

        DxilContainerHash.Sign(file);

        Assert.Equal("f5d8c1a64ee509b20c95d89f0fb3bb9f", Convert.ToHexStringLower(File.ReadAllBytes(file).AsSpan(4, 16)));
    }

    [Fact]
    public void Sign_RefusesWhatIsntADxbcContainer()
    {
        byte[] container = Container(64);
        container[0] = (byte)'X';

        var error = Assert.Throws<ContentBuildException>(() => DxilContainerHash.Sign(container, "a.dxil"));

        Assert.Contains("a.dxil", error.Message, StringComparison.Ordinal);
        Assert.Throws<ContentBuildException>(() => DxilContainerHash.Sign(new byte[16], "short.dxil"));
    }

    [Fact]
    public void Sign_RefusesAContainerWhoseSizeIsWrong()
    {
        byte[] container = Container(64);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(24), 100);

        var error = Assert.Throws<ContentBuildException>(() => DxilContainerHash.Sign(container, "a.dxil"));

        Assert.Contains("100", error.Message, StringComparison.Ordinal);
    }
}
