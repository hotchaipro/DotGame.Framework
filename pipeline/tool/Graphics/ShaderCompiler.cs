// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Compiles a Slang shader to one or more shader output types.
/// </summary>
/// <remarks>
/// <para>
/// Each stage is compiled once from Slang to SPIR-V with slangc, remapped to the
/// SDL3 GPU descriptor layout, and then converted to every other requested type:
/// with spirv-cross to MSL and GLSL, and with Mesa's spirv2dxil to DXIL, so every
/// type derives from the same SPIR-V.
/// </para>
/// <para>
/// The vertex stage is optional: it is only compiled when the shader declares a
/// [shader("vertex")] entry point.  The fragment stage is required.  A
/// fragment-only shader is paired at runtime with the engine's default sprite
/// vertex shader.
/// </para>
/// </remarks>
internal sealed partial class ShaderCompiler
{
    public required string SlangC { get; init; }

    public required string SpirvCross { get; init; }

    public required string Spirv2Dxil { get; init; }

    /// <summary>
    /// Parses a list of shader types separated by commas, semicolons or white
    /// space, e.g. "spirv,msl,dxil".
    /// </summary>
    public static ShaderTypes ParseShaderTypes(string text)
    {
        var types = ShaderTypes.None;
        foreach (string name in text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            types |= name.ToLowerInvariant() switch
            {
                "spirv" => ShaderTypes.SpirV,
                "msl" => ShaderTypes.Msl,
                "dxil" => ShaderTypes.Dxil,
                "glsl" => ShaderTypes.Glsl,
                _ => throw new ContentBuildException($"Unknown shader type '{name}'. Valid types: spirv, msl, dxil, glsl."),
            };
        }
        if (types == ShaderTypes.None)
        {
            throw new ContentBuildException("No shader type specified. Valid types: spirv, msl, dxil, glsl.");
        }
        return types;
    }

    /// <summary>
    /// Compiles <paramref name="inputFile"/>.  All outputs are written to
    /// <paramref name="outputDir"/>, named after <paramref name="baseName"/>.
    /// </summary>
    /// <param name="intermediateDir">
    /// Directory for intermediate files: SPIR-V when spirv is not requested, and
    /// per-stage GLSL.
    /// </param>
    public void Compile(string inputFile, string outputDir, string baseName, string intermediateDir, ShaderTypes types)
    {
        foreach (string directory in new[] { outputDir, intermediateDir })
        {
            if (directory.Length > 0)
            {
                Directory.CreateDirectory(directory);
            }
        }

        string source = File.ReadAllText(inputFile);
        string? vertexEntry = VertexEntryPointRegex().Match(source) is { Success: true } vertexMatch ? vertexMatch.Groups[1].Value : null;
        string fragmentEntry = FragmentEntryPointRegex().Match(source) is { Success: true } fragmentMatch
            ? fragmentMatch.Groups[1].Value
            : throw new ContentBuildException($"No [shader(\"pixel\")] or [shader(\"fragment\")] entry point found in '{inputFile}'.");
        bool hasVertex = vertexEntry is not null;

        // SPIR-V is a deliverable for the spirv type and an intermediate for the others.
        string spvDir = types.HasFlag(ShaderTypes.SpirV) ? outputDir : intermediateDir;
        string vertexSpv = Path.Combine(spvDir, $"{baseName}.vert.spv");
        string fragmentSpv = Path.Combine(spvDir, $"{baseName}.frag.spv");
        if (hasVertex)
        {
            this.CompileSpirV(inputFile, ShaderStage.Vertex, vertexEntry!, vertexSpv);
        }
        this.CompileSpirV(inputFile, ShaderStage.Fragment, fragmentEntry, fragmentSpv);

        if (types.HasFlag(ShaderTypes.Msl))
        {
            if (hasVertex)
            {
                this.CompileMsl(vertexSpv, Path.Combine(outputDir, $"{baseName}.vert.msl"), "vertex", inputFile);
            }
            this.CompileMsl(fragmentSpv, Path.Combine(outputDir, $"{baseName}.frag.msl"), "fragment", inputFile);
        }

        if (types.HasFlag(ShaderTypes.Dxil))
        {
            this.CompileDxil(inputFile, outputDir, baseName, hasVertex, vertexSpv, fragmentSpv);
        }

        if (types.HasFlag(ShaderTypes.Glsl))
        {
            this.CompileGlsl(inputFile, outputDir, baseName, intermediateDir, hasVertex, vertexSpv, fragmentSpv);
        }
    }

    private void CompileSpirV(string inputFile, ShaderStage stage, string entryPoint, string spvFile)
    {
        string stageName = stage == ShaderStage.Vertex ? "vertex" : "fragment";
        // SPIR-V 1.3: SDL creates a Vulkan 1.1 device, which accepts SPIR-V up to 1.3. slangc's own
        // default is newer (1.5), which the Vulkan validation layers reject and a driver may too.
        ExternalProcess.Run(this.SlangC, [inputFile, "-target", "spirv", "-profile", "spirv_1_3", "-stage", stageName, "-entry", entryPoint, "-no-mangle", "-o", spvFile],
            $"slangc compiling the {stageName} stage of '{inputFile}'");
        SpirvDescriptorRemapper.Remap(spvFile, stage);
    }

    /// <summary>
    /// Cross-compiles one stage to MSL. spirv-cross writes text files with the machine's line
    /// endings (CRLF on Windows), so the file is rewritten with LF: the same bytes on every machine.
    /// </summary>
    private void CompileMsl(string spvFile, string mslFile, string stageName, string inputFile)
    {
        this.CrossCompile(["--msl", "--msl-version", "20100", spvFile, "--output", mslFile], $"{stageName} SPIR-V to MSL", inputFile);
        string msl = File.ReadAllText(mslFile);
        if (msl.Contains('\r', StringComparison.Ordinal))
        {
            File.WriteAllText(mslFile, msl.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }

    private void CrossCompile(List<string> arguments, string conversion, string inputFile) =>
        ExternalProcess.Run(this.SpirvCross, arguments, $"spirv-cross converting {conversion} for '{inputFile}'");

    /// <summary>
    /// Converts each stage's SPIR-V to DXIL with Mesa's spirv2dxil, and signs the
    /// containers with the hash D3D12 checks.
    /// </summary>
    private void CompileDxil(string inputFile, string outputDir, string baseName, bool hasVertex, string vertexSpv, string fragmentSpv)
    {
        // Check the fragment stage's input against the vertex stage it is paired
        // with at runtime: its own vertex entry point, or the engine's default
        // sprite vertex shader (Color at location 0, TextureCoordinate at 1 and the
        // sprite's shader data at 2). A pixel shader may read a prefix of these, so
        // one that ignores the shader data still links.
        int[] fragmentLocations = SpirvInterface.Locations(fragmentSpv, outputs: false);
        if (hasVertex)
        {
            ThrowIfStageSignaturesMisaligned(fragmentLocations, SpirvInterface.Locations(vertexSpv, outputs: true), "the vertex shader", inputFile);
        }
        else
        {
            ThrowIfStageSignaturesMisaligned(fragmentLocations, [0, 1, 2], "the default sprite vertex shader", inputFile);
        }

        if (hasVertex)
        {
            this.ConvertToDxil(vertexSpv, ShaderStage.Vertex, Path.Combine(outputDir, $"{baseName}.vert.dxil"), inputFile);
        }
        this.ConvertToDxil(fragmentSpv, ShaderStage.Fragment, Path.Combine(outputDir, $"{baseName}.frag.dxil"), inputFile);
    }

    private void ConvertToDxil(string spvFile, ShaderStage stage, string dxilFile, string inputFile)
    {
        // slangc names every entry point main in SPIR-V. Shader model 6.0, as SDL's D3D12 backend asks.
        string stageName = stage == ShaderStage.Vertex ? "vertex" : "fragment";
        ExternalProcess.Run(this.Spirv2Dxil, ["-s", stageName, "-e", "main", "-m", "6.0", "-o", dxilFile, spvFile],
            $"spirv2dxil converting the {stageName} SPIR-V to DXIL for '{inputFile}'");
        DxilContainerHash.Sign(dxilFile);
    }

    /// <summary>
    /// The DXIL compilers pack a stage's varyings into consecutive registers, lowest
    /// location first, but D3D12 links stages by register, not by location.  When a
    /// pixel shader never reads one of the varyings, slangc eliminates it and the
    /// later varyings shift down a register; the resulting DXIL cannot link with
    /// the vertex stage, and the only symptom is an opaque "Could not create
    /// graphics pipeline state" error at runtime.  This detects that at build time.
    /// </summary>
    internal static void ThrowIfStageSignaturesMisaligned(int[] fragmentLocations, int[] vertexLocations, string vertexDescription, string sourceFile)
    {
        for (int slot = 0; slot < fragmentLocations.Length; slot++)
        {
            int actual = fragmentLocations[slot];
            int? expected = slot < vertexLocations.Length ? vertexLocations[slot] : null;
            if (actual == expected)
            {
                continue;
            }

            // Locations count the vertex output's fields after the position: 0 is the
            // first (the sprite's Color), 1 the second (its TextureCoordinate) and so on.
            string detail = expected is null
                ? $"the pixel shader reads the varying at location {actual}, which {vertexDescription} does not output"
                : $"the pixel shader reads the varying at location {actual} from input register {slot}, but {vertexDescription} writes location {expected} to that register";
            throw new ContentBuildException(
                $"Stage signature mismatch in '{sourceFile}': {detail}. " +
                "This happens when the pixel shader does not reference every field of the vertex stage output. " +
                "Unreferenced varyings are eliminated by the shader compiler, which shifts the remaining varyings into different input registers " +
                "and breaks D3D12 stage linkage at pipeline creation time. " +
                "Reference every field of the vertex output struct in the pixel shader (for example, multiply the result by 'input.Color').");
        }
    }

    /// <summary>
    /// Cross-compiles SPIR-V to GLSL ES 300 and combines the stages into one file
    /// with #ifdef VERTEX / #ifdef FRAGMENT sections.
    /// </summary>
    private void CompileGlsl(string inputFile, string outputDir, string baseName, string intermediateDir, bool hasVertex, string vertexSpv, string fragmentSpv)
    {
        // The varyings (location 0 = Color, location 1 = TextureCoordinate, location 2 =
        // ShaderData) are given the same names in both stages so they link.
        string vertexGlsl = Path.Combine(intermediateDir, $"{baseName}.vert.glsl");
        string fragmentGlsl = Path.Combine(intermediateDir, $"{baseName}.frag.glsl");
        if (hasVertex)
        {
            this.CrossCompile(["--version", "300", "--es", "--glsl-emit-ubo-as-plain-uniforms",
                "--rename-interface-variable", "out", "0", "v_Color",
                "--rename-interface-variable", "out", "1", "v_TextureCoordinate",
                "--rename-interface-variable", "out", "2", "v_ShaderData",
                vertexSpv, "--output", vertexGlsl], "vertex SPIR-V to GLSL", inputFile);
        }
        this.CrossCompile(["--version", "300", "--es", "--glsl-emit-ubo-as-plain-uniforms",
            "--rename-interface-variable", "in", "0", "v_Color",
            "--rename-interface-variable", "in", "1", "v_TextureCoordinate",
            "--rename-interface-variable", "in", "2", "v_ShaderData",
            fragmentSpv, "--output", fragmentGlsl], "fragment SPIR-V to GLSL", inputFile);

        string fragmentSource = ReadGlsl(fragmentGlsl);
        string output = hasVertex
            ? $"#ifdef VERTEX\n{ReadGlsl(vertexGlsl)}\n\n#endif\n#ifdef FRAGMENT\n{fragmentSource}\n\n#endif\n"
            // A fragment-only shader is paired with the sprite batch's vertex shader at runtime.
            : $"#ifdef FRAGMENT\n{fragmentSource}\n\n#endif\n";
        File.WriteAllText(Path.Combine(outputDir, $"{baseName}.glsl"), output);
    }

    private static string ReadGlsl(string path) =>
        FlattenStructUniforms(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)).Trim();

    /// <summary>
    /// Flattens struct uniforms into individual uniforms, so GL uniform names match
    /// the simple names used in C#.  spirv-cross with
    /// --glsl-emit-ubo-as-plain-uniforms emits a constant buffer as
    /// <code>
    ///   struct SLANG_ParameterGroup_XXX_std140 { highp vec2 TextureSize; highp float PxRange; };
    ///   uniform SLANG_ParameterGroup_XXX_std140 GlobalParams;
    /// </code>
    /// which becomes
    /// <code>
    ///   uniform highp vec2 TextureSize;
    ///   uniform highp float PxRange;
    /// </code>
    /// with every "GlobalParams.member" reference replaced by "member".
    /// </summary>
    internal static string FlattenStructUniforms(string glsl)
    {
        while (StructUniformRegex().Match(glsl) is { Success: true } match)
        {
            string fullMatch = match.Groups[1].Value;
            string instanceName = match.Groups[4].Value;
            var declarations = new List<string>();
            var memberNames = new List<string>();
            foreach (string part in match.Groups[3].Value.Split(';'))
            {
                string declaration = part.Trim();
                if (declaration.Length == 0)
                {
                    continue;
                }

                // NOTE: One left out would still be used, and fail to compile only at runtime
                if (StructMemberRegex().Match(declaration) is not { Success: true } member)
                {
                    throw new ContentBuildException($"The uniform '{declaration}' in a shader's parameters can't be flattened for GLSL.");
                }

                declarations.Add($"uniform {member.Groups[1].Value.Trim()} {member.Groups[2].Value}{member.Groups[3].Value};");
                memberNames.Add(member.Groups[2].Value);
            }

            glsl = glsl.Replace(fullMatch, string.Join('\n', declarations), StringComparison.Ordinal);
            foreach (string memberName in memberNames)
            {
                glsl = glsl.Replace($"{instanceName}.{memberName}", memberName, StringComparison.Ordinal);
            }
        }
        return glsl;
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // NOTE: Not on a commented-out line, and with any other attributes between it and the function
    [GeneratedRegex("""(?m)^(?:(?!//).)*?\[shader\("vertex"\)\]\s*(?:\[[^\]]*\]\s*)*\S+\s+(\w+)\s*\(""", Options)]
    internal static partial Regex VertexEntryPointRegex();

    [GeneratedRegex("""(?m)^(?:(?!//).)*?\[shader\("(?:pixel|fragment)"\)\]\s*(?:\[[^\]]*\]\s*)*\S+\s+(\w+)\s*\(""", Options)]
    internal static partial Regex FragmentEntryPointRegex();

    [GeneratedRegex(@"(struct\s+(\w+)\s*\{([^}]+)\};\s+uniform\s+\2\s+(\w+)\s*;)", Options | RegexOptions.Singleline)]
    private static partial Regex StructUniformRegex();

    [GeneratedRegex(@"^(.+\s+)(\w+)((?:\s*\[\s*\d*\s*\])*)$", Options)]
    private static partial Regex StructMemberRegex();
}
