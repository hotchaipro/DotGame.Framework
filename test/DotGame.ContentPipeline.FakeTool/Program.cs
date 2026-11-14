// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace DotGame.ContentPipeline.FakeTool;

/// <summary>
/// Stands in for an encoder or an external tool. The pipeline's tests point processor commands
/// and tool paths at it.
/// </summary>
/// <remarks>
/// <para>
/// As a processor: <c>FakeContentTool &lt;kind&gt; --input a --output b [--name value]...</c> writes
/// an output that depends only on the input's bytes and the other options. The kind
/// <c>shader</c> writes every desktop stage file, the primary (<c>.frag.spv</c>) first, as the
/// real shader command does.
/// </para>
/// <para>
/// As a tool, recognized by its arguments: ffmpeg, astcenc, slangc, spirv-cross and
/// spirv2dxil, each writing a plausible output file (an unsigned DXBC container from
/// spirv2dxil). An ffmpeg decode to WAV (<c>-f wav</c>) writes a real WAV of the input's
/// duration, and the processor <c>music</c> starts its output with a FLAC header giving the
/// output's duration, at 44.1 kHz in stereo, for the pipeline to read.
/// </para>
/// <para>
/// Its behavior comes from a <c>fake-tool.config</c> file found by walking up from any path in
/// its arguments, so each test configures its own directory: <c>fail.&lt;kind&gt;=exit</c> fails
/// without writing, <c>partial</c> writes half an output first, <c>after-primary</c> (shaders)
/// writes the primary output first, <c>hang</c> writes half an output and then waits to be
/// killed. <c>text.&lt;name&gt;=path</c> makes a tool write that file's text to an output named
/// <c>name</c> (spirv-cross), and <c>spirv.&lt;name&gt;=path</c> makes slangc write that file's
/// bytes. <c>duration.&lt;file name&gt;=seconds</c> sets those durations (1.5 by default): of the
/// input for ffmpeg, of the output for <c>music</c>; one that isn't a number makes a file that
/// isn't audio. <c>copy.ffmpeg=1</c> makes an ffmpeg encode copy its input to its output, so a
/// test can read what the build tool handed the encoder. Every
/// run appends a line to <c>calls.log</c> beside the config: the kind, a tab, the input's file
/// name, a tab, the arguments.
/// </para>
/// </remarks>
internal static class Program
{
    private const string ConfigFileName = "fake-tool.config";

    private static int Main(string[] args)
    {
        string? configDir = FindConfigDir(args);
        var config = ReadConfig(configDir);
        string kind = Classify(args);
        string input = Option(args, "--input") ?? FirstExistingFile(args) ?? "";

        if (configDir is not null)
        {
            string line = $"{kind}\t{Path.GetFileName(input)}\t{string.Join(' ', args)}\n";
            AppendWithRetry(Path.Combine(configDir, "calls.log"), line);
        }

        // A named input that isn't there fails, as with every real encoder
        if ((Option(args, "--input") is { } named) && !File.Exists(named))
        {
            Console.Error.WriteLine($"FakeContentTool: error FAKE0002: input not found: {named}");
            return 2;
        }

        config.TryGetValue($"fail.{kind}", out string? failure);
        string? output = OutputOf(kind, args);

        if (failure == "exit")
        {
            return Fail(kind);
        }

        if ((failure is "partial" or "hang") && (output is not null))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, "PARTIAL");
            if (failure == "hang")
            {
                File.WriteAllText(Path.Combine(configDir!, "hang.started"), "");

                // Until the test kills it, or for long enough that a lost one still ends
                Thread.Sleep(TimeSpan.FromMinutes(15));
            }

            return Fail(kind);
        }

        switch (kind)
        {
            case "ffmpeg" when Option(args, "-f") == "wav":
                WriteWav(output!, Duration(config, input));
                return 0;
            case "ffmpeg" when config.ContainsKey("copy.ffmpeg") && (output is not null):
                // What the build tool hands an encoder, for the test to read back
                File.Copy(input, output, overwrite: true);
                return 0;
            case "music" when output is not null:
                WriteMusic(output, Duration(config, output), Describe(kind, input, args));
                return 0;
            case "shader":
                return WriteShader(args, input, failure);
            case "slangc":
                WriteSpirV(output!, config);
                return 0;
            case "spirv2dxil":
                WriteDxil(output!, Describe(kind, input, args));
                return 0;
            case "spirv-cross":
                WriteText(output!, config);
                return 0;
        }

        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, Describe(kind, input, args));
        }

        return 0;
    }

    private static int Fail(string kind)
    {
        Console.Error.WriteLine($"FakeContentTool: error FAKE0001: {kind} failed as configured");
        return 3;
    }

    // The real tools, by the arguments the build tool gives them; else the first argument names a processor
    private static string Classify(string[] args)
    {
        if (args.Length == 0)
        {
            return "none";
        }

        if (args.Contains("-hide_banner"))
        {
            return "ffmpeg";
        }

        if (args.Contains("-target") && args.Contains("-entry"))
        {
            return "slangc";
        }

        if (args.Contains("--msl") || args.Contains("--hlsl") || args.Contains("--es"))
        {
            return "spirv-cross";
        }

        if (args.Contains("-s") && args.Contains("-m") && args.Contains("-o"))
        {
            return "spirv2dxil";
        }

        if (args[0] == "-cl")
        {
            return "astcenc";
        }

        return args[0];
    }

    private static string? OutputOf(string kind, string[] args)
    {
        return kind switch
        {
            "ffmpeg" => args[^1],
            "slangc" => Option(args, "-o"),
            "spirv-cross" => Option(args, "--output"),
            "spirv2dxil" => Option(args, "-o"),
            "astcenc" => args[2],
            _ => Option(args, "--output"),
        };
    }

    // An output that changes with the input's bytes and with every option but the paths
    private static string Describe(string kind, string input, string[] args)
    {
        var text = new StringBuilder();
        text.Append("FAKE ").Append(kind).Append('\n');
        text.Append(File.Exists(input) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))) : "no-input").Append('\n');
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--input" or "--output" or "--intermediate-dir")
            {
                i++;
            }
            else if (!Path.IsPathRooted(args[i]))
            {
                text.Append(args[i]).Append(' ');
            }
        }

        return text.ToString();
    }

    private static int WriteShader(string[] args, string input, string? failure)
    {
        string output = Option(args, "--output")!;
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        string baseName = Path.GetFileName(output);
        baseName = baseName[..baseName.IndexOf('.', StringComparison.Ordinal)];
        string source = File.ReadAllText(input);

        File.WriteAllText(output, Describe("shader.frag.spv", input, args));
        if (failure == "after-primary")
        {
            return Fail("shader");
        }

        // A shader that declares no vertex stage has no vertex outputs
        bool hasVertex = source.Contains("[shader(\"vertex\")]", StringComparison.Ordinal);
        foreach (string suffix in new[] { ".vert.spv", ".frag.msl", ".vert.msl", ".frag.dxil", ".vert.dxil" })
        {
            if (hasVertex || !suffix.StartsWith(".vert", StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(directory, baseName + suffix), Describe("shader" + suffix, input, args));
            }
        }

        return 0;
    }

    // A SPIR-V header, which the descriptor remapper reads and leaves alone
    private static void WriteSpirV(string output, Dictionary<string, string> config)
    {
        if (config.TryGetValue($"spirv.{Path.GetFileName(output)}", out string? path))
        {
            File.Copy(path, output, overwrite: true);
            return;
        }

        using var file = new BinaryWriter(File.Create(output));
        foreach (uint word in new uint[] { 0x07230203, 0x00010000, 0, 1, 0 })
        {
            file.Write(word);
        }
    }

    // A DXBC container as spirv2dxil writes it: the hash left zero, one part holding the description
    private static void WriteDxil(string output, string description)
    {
        byte[] part = Encoding.UTF8.GetBytes(description);
        const int HeaderSize = 32 + 4 + 8;
        using var file = new BinaryWriter(File.Create(output));
        file.Write("DXBC"u8);
        file.Write(new byte[16]);
        file.Write((ushort)1);
        file.Write((ushort)0);
        file.Write((uint)(HeaderSize + part.Length));
        file.Write(1u);
        file.Write(32u + 4);
        file.Write("FAKE"u8);
        file.Write((uint)part.Length);
        file.Write(part);
    }

    private static void WriteText(string output, Dictionary<string, string> config)
    {
        string name = Path.GetFileName(output);
        string text = config.TryGetValue($"text.{name}", out string? path) ? File.ReadAllText(path) : $"// fake {name}\n";
        File.WriteAllText(output, text);
    }

    private static double? Duration(Dictionary<string, string> config, string file)
    {
        string text = config.TryGetValue($"duration.{Path.GetFileName(file)}", out string? value) ? value : "1.5";
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : null;
    }

    // 8-bit mono at 48 kHz, which keeps it small: only the frame count matters
    private static void WriteWav(string output, double? seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (seconds is not { } duration)
        {
            File.WriteAllText(output, "FAKE not audio");
            return;
        }

        const int Rate = 48000;
        int frames = (int)Math.Round(duration * Rate);
        using var file = new BinaryWriter(File.Create(output));
        file.Write("RIFF"u8);
        file.Write(36 + frames);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)1);
        file.Write(Rate);
        file.Write(Rate);
        file.Write((short)1);
        file.Write((short)8);
        file.Write("data"u8);
        file.Write(frames);
        file.Write(new byte[frames]);
    }

    // A FLAC STREAMINFO block, then the description: the pipeline reads only the block
    private static void WriteMusic(string output, double? seconds, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (seconds is not { } duration)
        {
            File.WriteAllText(output, description);
            return;
        }

        const int Rate = 44100;
        const int Channels = 2;
        long samples = (long)Math.Round(duration * Rate);
        var info = new byte[34];
        info[10] = (byte)(Rate >> 12);
        info[11] = (byte)((Rate >> 4) & 0xFF);
        info[12] = (byte)(((Rate & 0xF) << 4) | ((Channels - 1) << 1));
        info[13] = (byte)((15 << 4) | (int)((samples >> 32) & 0xF));
        info[14] = (byte)(samples >> 24);
        info[15] = (byte)(samples >> 16);
        info[16] = (byte)(samples >> 8);
        info[17] = (byte)samples;
        using var file = File.Create(output);
        file.Write("fLaC"u8);
        file.Write([0x80, 0, 0, 34]);
        file.Write(info);
        file.Write(Encoding.UTF8.GetBytes(description));
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return (index >= 0) && (index + 1 < args.Length) ? args[index + 1] : null;
    }

    private static string? FirstExistingFile(string[] args)
    {
        foreach (string arg in args)
        {
            if (Path.IsPathRooted(arg) && File.Exists(arg))
            {
                return arg;
            }
        }

        return null;
    }

    private static string? FindConfigDir(string[] args)
    {
        foreach (string arg in args)
        {
            if (!Path.IsPathRooted(arg))
            {
                continue;
            }

            for (string? directory = Path.GetDirectoryName(arg); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (File.Exists(Path.Combine(directory, ConfigFileName)))
                {
                    return directory;
                }
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadConfig(string? configDir)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configDir is not null)
        {
            foreach (string line in File.ReadAllLines(Path.Combine(configDir, ConfigFileName)))
            {
                int separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0)
                {
                    config[line[..separator]] = line[(separator + 1)..];
                }
            }
        }

        return config;
    }

    // Two runs can log at once: each holds the file alone while it writes, and waits for the other.
    // NOTE: File.AppendAllText shares the file with other writers, and .NET appends by seeking to the
    // end, so on macOS and Linux two runs could write at one offset and tear a line
    private static void AppendWithRetry(string path, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(10);
            }
        }
    }
}
