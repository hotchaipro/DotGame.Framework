// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Reads the varyings a SPIR-V stage passes or receives: the locations of its
/// input or output variables.
/// </summary>
internal static class SpirvInterface
{
    private const uint OpVariable = 59;
    private const uint OpDecorate = 71;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationLocation = 30;
    private const uint StorageClassInput = 1;
    private const uint StorageClassOutput = 3;

    /// <summary>
    /// The locations of the stage's input (or output) variables, lowest first,
    /// leaving out built-ins such as the position. slangc leaves out a varying the
    /// stage never uses.
    /// </summary>
    public static int[] Locations(string spvPath, bool outputs)
    {
        byte[] bytes = File.ReadAllBytes(spvPath);
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);

        uint storageClass = outputs ? StorageClassOutput : StorageClassInput;
        var variables = new HashSet<uint>();
        var builtIns = new HashSet<uint>();
        var locations = new Dictionary<uint, int>();
        foreach (int i in SpirvDescriptorRemapper.Instructions(words))
        {
            uint opcode = SpirvDescriptorRemapper.Opcode(words[i]);
            int wordCount = SpirvDescriptorRemapper.WordCount(words[i]);
            if (opcode == OpVariable && wordCount >= 4 && words[i + 3] == storageClass)
            {
                variables.Add(words[i + 2]);
            }
            else if (opcode == OpDecorate && wordCount >= 3 && words[i + 2] == DecorationBuiltIn)
            {
                builtIns.Add(words[i + 1]);
            }
            else if (opcode == OpDecorate && wordCount >= 4 && words[i + 2] == DecorationLocation)
            {
                locations[words[i + 1]] = (int)words[i + 3];
            }
        }

        var result = new List<int>();
        foreach (uint variable in variables)
        {
            if (!builtIns.Contains(variable) && locations.TryGetValue(variable, out int location))
            {
                result.Add(location);
            }
        }

        result.Sort();
        return [.. result];
    }
}
