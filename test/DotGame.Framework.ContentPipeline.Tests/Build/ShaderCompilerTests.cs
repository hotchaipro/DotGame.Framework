// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// Shader parsing, and the compile sequence with the fake tool in place of slangc, spirv-cross and
/// spirv2dxil.
/// </summary>
public sealed class ShaderCompilerTests
{
    private const string VertexAndFragment = """
        [shader("vertex")]
        VertexOutput vertexMain(VertexInput input) { }

        [shader("pixel")]
        float4 fragmentMain(VertexOutput input) : SV_Target { }
        """;

    private const string FragmentOnly = """
        [shader("fragment")] float4 main(VertexOutput input) { }
        """;

    private static ShaderCompiler Compiler() => new() { SlangC = Paths.FakeTool, SpirvCross = Paths.FakeTool, Spirv2Dxil = Paths.FakeTool };

    [Theory]
    [InlineData("spirv", 1)]
    [InlineData("SPIRV,msl", 1 | 2)]
    [InlineData("spirv;msl dxil\tglsl", 1 | 2 | 4 | 8)]
    public void ParseShaderTypes_ReadsAList(string text, int expected)
    {
        Assert.Equal((ShaderTypes)expected, ShaderCompiler.ParseShaderTypes(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    [InlineData("hlsl")]
    public void ParseShaderTypes_RefusesNothingOrAnUnknownType(string text)
    {
        Assert.Throws<ContentBuildException>(() => ShaderCompiler.ParseShaderTypes(text));
    }

    [Fact]
    public void EntryPoints_AreFoundWithTheirNames()
    {
        Assert.Equal("vertexMain", ShaderCompiler.VertexEntryPointRegex().Match(VertexAndFragment).Groups[1].Value);
        Assert.Equal("fragmentMain", ShaderCompiler.FragmentEntryPointRegex().Match(VertexAndFragment).Groups[1].Value);
        Assert.Equal("main", ShaderCompiler.FragmentEntryPointRegex().Match(FragmentOnly).Groups[1].Value);
        Assert.DoesNotMatch(ShaderCompiler.VertexEntryPointRegex(), FragmentOnly);
    }

    [Fact]
    public void EntryPoints_IgnoreCommentedOutCode()
    {
        const string Source = """
            // [shader("vertex")] VertexOutput oldVertex(VertexInput input) { }

            [shader("pixel")]
            float4 main(VertexOutput input) { }
            """;

        Assert.DoesNotMatch(ShaderCompiler.VertexEntryPointRegex(), Source);
    }

    [Fact]
    public void EntryPoints_AreFoundWithAnotherAttributeBeforeThem()
    {
        const string Source = """
            [shader("pixel")]
            [ForceInline]
            float4 main(VertexOutput input) { }
            """;

        Assert.Equal("main", ShaderCompiler.FragmentEntryPointRegex().Match(Source).Groups[1].Value);
    }

    [Fact]
    public void FlattenStructUniforms_TurnsMembersIntoUniforms_AndRenamesTheirUses()
    {
        const string Glsl = """
            struct SLANG_ParameterGroup_GlobalParams_std140
            {
                highp vec2 TextureSize;
                highp float PxRange;
            };

            uniform SLANG_ParameterGroup_GlobalParams_std140 GlobalParams;

            void main() { x = GlobalParams.TextureSize * GlobalParams.PxRange; }
            """;

        string flattened = ShaderCompiler.FlattenStructUniforms(Glsl.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.Contains("uniform highp vec2 TextureSize;", flattened, StringComparison.Ordinal);
        Assert.Contains("uniform highp float PxRange;", flattened, StringComparison.Ordinal);
        Assert.Contains("x = TextureSize * PxRange;", flattened, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalParams", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void FlattenStructUniforms_LeavesOtherCodeAlone()
    {
        const string Glsl = "struct Local { highp float x; };\nLocal value;\nvoid main() { }\n";

        Assert.Equal(Glsl, ShaderCompiler.FlattenStructUniforms(Glsl));
    }

    [Fact]
    public void FlattenStructUniforms_KeepsArrayMembers()
    {
        const string Glsl = "struct Params_std140\n{\n    highp vec4 Colors[4];\n};\n\nuniform Params_std140 Params;\n\nvoid main() { x = Params.Colors[1]; }\n";

        string flattened = ShaderCompiler.FlattenStructUniforms(Glsl);

        Assert.Contains("uniform highp vec4 Colors[4];", flattened, StringComparison.Ordinal);
        Assert.Contains("x = Colors[1];", flattened, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2 }, new[] { 0, 1, 2 })]
    [InlineData(new[] { 0, 1 }, new[] { 0, 1, 2 })]
    [InlineData(new int[0], new[] { 0, 1, 2 })]
    public void StageSignatures_ThatLineUp_Pass(int[] fragment, int[] vertex)
    {
        ShaderCompiler.ThrowIfStageSignaturesMisaligned(fragment, vertex, "the vertex shader", "a.slang");
    }

    [Fact]
    public void StageSignatures_ShiftedByAnEliminatedVarying_FailWithTheFix()
    {
        var error = Assert.Throws<ContentBuildException>(() => ShaderCompiler.ThrowIfStageSignaturesMisaligned([1], [0, 1, 2], "the vertex shader", "a.slang"));

        Assert.Contains("Stage signature mismatch in 'a.slang'", error.Message, StringComparison.Ordinal);
        Assert.Contains("reads the varying at location 1 from input register 0", error.Message, StringComparison.Ordinal);
        Assert.Contains("Reference every field", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StageSignatures_ReadingWhatTheVertexStageDoesntWrite_Fail()
    {
        var error = Assert.Throws<ContentBuildException>(() => ShaderCompiler.ThrowIfStageSignaturesMisaligned([0, 1, 2], [0, 1], "the default sprite vertex shader", "a.slang"));

        Assert.Contains("reads the varying at location 2, which", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_WithNoFragmentEntryPoint_NamesTheFile()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("broken.slang", "// nothing here");

        var error = Assert.Throws<ContentBuildException>(() => Compiler().Compile(source, directory.Path_("out"), "broken", directory.Path_("obj"), ShaderTypes.SpirV));

        Assert.Contains("broken.slang", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_CompilesBothStagesWithTheirEntryPoints()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);

        Compiler().Compile(source, directory.Path_("out"), "glow", directory.Path_("obj"), ShaderTypes.SpirV);

        List<ToolCall> slangc = directory.ToolCalls().FindAll(call => call.Kind == "slangc");
        Assert.Equal(2, slangc.Count);
        Assert.Contains("-stage vertex -entry vertexMain", slangc[0].Arguments, StringComparison.Ordinal);
        Assert.Contains("-stage fragment -entry fragmentMain", slangc[1].Arguments, StringComparison.Ordinal);
        Assert.True(File.Exists(directory.Path_("out", "glow.vert.spv")));
        Assert.True(File.Exists(directory.Path_("out", "glow.frag.spv")));
    }

    [Fact]
    public void Compile_OfAFragmentOnlyShader_HasNoVertexOutputs()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("tint.slang", FragmentOnly);

        Compiler().Compile(source, directory.Path_("out"), "tint", directory.Path_("obj"), ShaderTypes.SpirV | ShaderTypes.Msl | ShaderTypes.Dxil);

        Assert.Equal(["tint.frag.dxil", "tint.frag.msl", "tint.frag.spv"], Directory.GetFiles(directory.Path_("out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Compile_OfOtherTypes_KeepsSpirVInTheIntermediateFolder()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);

        Compiler().Compile(source, directory.Path_("out"), "glow", directory.Path_("obj"), ShaderTypes.Msl);

        Assert.False(File.Exists(directory.Path_("out", "glow.frag.spv")));
        Assert.True(File.Exists(directory.Path_("obj", "glow.frag.spv")));
        Assert.True(File.Exists(directory.Path_("out", "glow.vert.msl")));
    }

    [Fact]
    public void Compile_ToGlsl_CombinesTheStagesInOneFile()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);
        string vertex = directory.WriteFile("vertex.txt", "#version 300 es\nvoid vertexCode() { }\n");
        string fragment = directory.WriteFile("fragment.txt", "#version 300 es\nvoid fragmentCode() { }\n");
        directory.SetToolConfig("text.glow.vert.glsl", vertex);
        directory.SetToolConfig("text.glow.frag.glsl", fragment);

        Compiler().Compile(source, directory.Path_("out"), "glow", directory.Path_("obj"), ShaderTypes.Glsl);

        string glsl = File.ReadAllText(directory.Path_("out", "glow.glsl"));
        Assert.True(glsl.IndexOf("#ifdef VERTEX", StringComparison.Ordinal) < glsl.IndexOf("vertexCode", StringComparison.Ordinal));
        Assert.True(glsl.IndexOf("#ifdef FRAGMENT", StringComparison.Ordinal) < glsl.IndexOf("fragmentCode", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', glsl);
    }

    [Fact]
    public void Compile_ToGlsl_OfAFragmentOnlyShader_HasNoVertexSection()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("tint.slang", FragmentOnly);

        Compiler().Compile(source, directory.Path_("out"), "tint", directory.Path_("obj"), ShaderTypes.Glsl);

        Assert.DoesNotContain("#ifdef VERTEX", File.ReadAllText(directory.Path_("out", "tint.glsl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ToDxil_ChecksTheStageSignaturesFromTheSpirV()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);

        // The vertex stage writes locations 0 and 1 (and the position); the pixel shader reads only 1
        directory.SetToolConfig("spirv.glow.vert.spv", WriteSpirv(directory, "vertex.spv",
            Variable(10, StorageClassOutput), Decorate(10, DecorationLocation, 0),
            Variable(11, StorageClassOutput), Decorate(11, DecorationLocation, 1),
            Variable(12, StorageClassOutput), Decorate(12, DecorationBuiltIn, 0)));
        directory.SetToolConfig("spirv.glow.frag.spv", WriteSpirv(directory, "fragment.spv",
            Variable(20, StorageClassInput), Decorate(20, DecorationLocation, 1)));

        var error = Assert.Throws<ContentBuildException>(() => Compiler().Compile(source, directory.Path_("out"), "glow", directory.Path_("obj"), ShaderTypes.Dxil));

        Assert.Contains("Stage signature mismatch", error.Message, StringComparison.Ordinal);
        Assert.Contains("location 1 from input register 0", error.Message, StringComparison.Ordinal);
        Assert.Empty(directory.ToolCalls().FindAll(call => call.Kind == "spirv2dxil"));
    }

    [Fact]
    public void Compile_ToDxil_ConvertsEachStageWithSpirv2Dxil_AndSignsIt()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);

        Compiler().Compile(source, directory.Path_("out"), "glow", directory.Path_("obj"), ShaderTypes.Dxil);

        List<ToolCall> calls = directory.ToolCalls().FindAll(call => call.Kind == "spirv2dxil");
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("-s vertex -e main -m 6.0 -o ", calls[0].Arguments, StringComparison.Ordinal);
        Assert.StartsWith("-s fragment -e main -m 6.0 -o ", calls[1].Arguments, StringComparison.Ordinal);
        foreach (string stage in new[] { "vert", "frag" })
        {
            byte[] container = File.ReadAllBytes(directory.Path_("out", $"glow.{stage}.dxil"));
            byte[] unsigned = (byte[])container.Clone();
            unsigned.AsSpan(4, 16).Clear();
            DxilContainerHash.Sign(unsigned, "expected");
            Assert.NotEqual(new byte[16], container.AsSpan(4, 16).ToArray());
            Assert.Equal(unsigned, container);
        }
    }

    [Fact]
    public void SpirvInterface_ReadsLocationsLowestFirst_AndLeavesOutBuiltIns()
    {
        using var directory = new TestDirectory();
        string file = WriteSpirv(directory, "a.spv",
            Variable(10, StorageClassInput), Decorate(10, DecorationLocation, 2),
            Variable(11, StorageClassInput), Decorate(11, DecorationLocation, 0),
            Variable(12, StorageClassInput), Decorate(12, DecorationBuiltIn, 15),
            Variable(13, StorageClassOutput), Decorate(13, DecorationLocation, 1));

        Assert.Equal([0, 2], SpirvInterface.Locations(file, outputs: false));
        Assert.Equal([1], SpirvInterface.Locations(file, outputs: true));
    }

    [Fact]
    public void Compile_ReportsAFailedToolWithTheShadersName()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);
        directory.SetToolConfig("fail.spirv2dxil", "exit");

        var error = Assert.Throws<ContentBuildException>(() => Compiler().Compile(source, directory.Path_("out"), "glow", directory.Path_("obj"), ShaderTypes.Dxil));

        Assert.Contains("spirv2dxil", error.Message, StringComparison.Ordinal);
        Assert.Contains("glow.slang", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderCommand_TakesTheBaseNameFromAStageSuffixedOutput()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("glow.slang", VertexAndFragment);

        ShaderCommand.Run(CommandLineOptions.Parse(["--slangc", Paths.FakeTool, "--spirv-cross", Paths.FakeTool, "--spirv2dxil", Paths.FakeTool, "--shader-types", "spirv", "--input", source, "--output", directory.Path_("out", "glow.frag.spv")]));

        Assert.True(File.Exists(directory.Path_("out", "glow.frag.spv")));
        Assert.True(File.Exists(directory.Path_("out", "glow.vert.spv")));
    }

    [Fact]
    public void ShaderCommand_ToGlsl_WritesTheOutputItWasAskedFor()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("blur.frag.slang", FragmentOnly);

        ShaderCommand.Run(CommandLineOptions.Parse(["--slangc", Paths.FakeTool, "--spirv-cross", Paths.FakeTool, "--spirv2dxil", Paths.FakeTool, "--shader-types", "glsl", "--input", source, "--output", directory.Path_("out", "blur.frag.glsl"), "--intermediate-dir", directory.Path_("obj")]));

        Assert.True(File.Exists(directory.Path_("out", "blur.frag.glsl")));
    }

    private static byte[] Spirv(params uint[][] instructions)
    {
        uint[] words = [0x07230203, 0x00010000, 0, 100, 0, .. instructions.SelectMany(instruction => instruction)];
        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private const uint StorageClassInput = 1;
    private const uint StorageClassOutput = 3;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationLocation = 30;

    private static string WriteSpirv(TestDirectory directory, string name, params uint[][] instructions)
    {
        string file = directory.Path_(name);
        File.WriteAllBytes(file, Spirv(instructions));
        return file;
    }

    // OpDecorate <id> Binding|DescriptorSet|Location|BuiltIn <value>
    private static uint[] Decorate(uint id, uint decoration, uint value) => [(4u << 16) | 71, id, decoration, value];

    // OpVariable <type> <id> <storage class>
    private static uint[] Variable(uint id, uint storageClass) => [(4u << 16) | 59, 1, id, storageClass];

    private static uint[] Words(byte[] bytes)
    {
        var words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }

        return words;
    }

    [Theory]
    [InlineData(true, 0u, 1u)]
    [InlineData(false, 2u, 3u)]
    public void Remap_MovesResourcesToTheSetsSdlGpuExpects(bool vertexStage, uint samplerSet, uint uniformSet)
    {
        ShaderStage stage = vertexStage ? ShaderStage.Vertex : ShaderStage.Fragment;
        const uint Sampler = 10;
        const uint Uniform = 11;
        const uint DescriptorSet = 34;
        const uint Binding = 33;
        using var directory = new TestDirectory();
        string file = directory.Path_("a.spv");
        File.WriteAllBytes(file, Spirv(
            Decorate(Sampler, DescriptorSet, 0),
            Decorate(Sampler, Binding, 1),
            Decorate(Uniform, DescriptorSet, 0),
            Decorate(Uniform, Binding, 0),
            Variable(Sampler, 0),
            Variable(Uniform, 2)));

        SpirvDescriptorRemapper.Remap(file, stage);

        uint[] words = Words(File.ReadAllBytes(file));
        Assert.Equal(samplerSet, words[5 + 3]);
        Assert.Equal(uniformSet, words[5 + 8 + 3]);

        // Each alone in its set now, so each is renumbered to binding 0
        Assert.Equal(0u, words[5 + 4 + 3]);
        Assert.Equal(0u, words[5 + 12 + 3]);
    }

    [Fact]
    public void Remap_NumbersBindingsConsecutively_InTheirOrder()
    {
        const uint DescriptorSet = 34;
        const uint Binding = 33;
        using var directory = new TestDirectory();
        string file = directory.Path_("a.spv");
        File.WriteAllBytes(file, Spirv(
            Decorate(10, DescriptorSet, 0),
            Decorate(10, Binding, 5),
            Decorate(11, DescriptorSet, 0),
            Decorate(11, Binding, 2),
            Variable(10, 0),
            Variable(11, 0)));

        SpirvDescriptorRemapper.Remap(file, ShaderStage.Fragment);

        uint[] words = Words(File.ReadAllBytes(file));
        Assert.Equal(1u, words[5 + 4 + 3]);
        Assert.Equal(0u, words[5 + 12 + 3]);
    }

    [Fact]
    public void Remap_LeavesAModuleWithNothingToChangeAlone()
    {
        using var directory = new TestDirectory();
        string file = directory.Path_("a.spv");
        byte[] module = Spirv(Variable(10, 7));
        File.WriteAllBytes(file, module);
        DateTime written = File.GetLastWriteTimeUtc(file);

        SpirvDescriptorRemapper.Remap(file, ShaderStage.Vertex);

        Assert.Equal(module, File.ReadAllBytes(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }
}
