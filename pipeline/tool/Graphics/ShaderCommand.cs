// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Compiles a Slang shader content item to the platform's shader types.
/// </summary>
internal static partial class ShaderCommand
{
    public const string Name = "shader";
    public const string ErrorCode = "SHD0001";

    public static void Run(CommandLineOptions options)
    {
        var compiler = new ShaderCompiler
        {
            SlangC = options.GetToolPath("slangc", "slangc"),
            SpirvCross = options.GetToolPath("spirv-cross", "spirv-cross"),
            Spirv2Dxil = options.GetToolPath("spirv2dxil", "spirv2dxil"),
        };
        ShaderTypes types = ShaderCompiler.ParseShaderTypes(options.GetRequired("shader-types"));
        string inputFile = options.GetPath("input");
        string outputFile = options.GetPath("output");
        string intermediateDir = ContentPaths.Normalize(options.GetString("intermediate-dir"));
        options.ThrowIfUnrecognized();

        // The output file is the one the build tracks (e.g. <name>.frag.spv or
        // <name>.glsl); every output is named after its base name without any
        // stage suffix. A .glsl output has none: a shader can be called blur.frag.
        string outputDir = Path.GetDirectoryName(outputFile) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(outputFile);
        bool hasStageSuffix = !outputFile.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase);
        if (hasStageSuffix && (StageSuffixRegex().Match(baseName) is { Success: true } match))
        {
            baseName = match.Groups[1].Value;
        }

        compiler.Compile(inputFile, outputDir, baseName, intermediateDir.Length > 0 ? intermediateDir : outputDir, types);
    }

    [GeneratedRegex(@"^(.+)\.(?:vert|frag)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StageSuffixRegex();
}
