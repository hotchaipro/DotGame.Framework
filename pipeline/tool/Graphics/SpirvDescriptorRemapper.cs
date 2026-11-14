// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Remaps the descriptor sets and bindings of a SPIR-V module to the fixed
/// layout SDL3 GPU requires.
/// </summary>
/// <remarks>
/// Without explicit register spaces, Slang puts every resource in descriptor
/// set 0 and numbers bindings across all resource types.  SDL3 GPU expects:
/// <code>
///   vertex:    set 0 = samplers (UniformConstant), set 1 = uniform buffers (Uniform)
///   fragment:  set 2 = samplers (UniformConstant), set 3 = uniform buffers (Uniform)
/// </code>
/// with consecutive bindings from 0 within each set.  The remapping does not
/// affect the MSL or GLSL cross-compiled from the module.
/// </remarks>
internal static class SpirvDescriptorRemapper
{
    internal const int HeaderWordCount = 5;
    private const uint OpVariable = 59;
    private const uint OpDecorate = 71;
    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;
    private const uint StorageClassUniformConstant = 0;
    private const uint StorageClassUniform = 2;

    /// <summary>Rewrites <paramref name="spvPath"/> in place when anything changes.</summary>
    public static void Remap(string spvPath, ShaderStage stage)
    {
        byte[] bytes = File.ReadAllBytes(spvPath);
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);

        // Pass 1: storage class of each variable, by result id.
        var storageClasses = new Dictionary<uint, uint>();
        foreach (int i in Instructions(words))
        {
            if (Opcode(words[i]) == OpVariable && WordCount(words[i]) >= 4)
            {
                storageClasses[words[i + 2]] = words[i + 3];
            }
        }

        // Pass 2: move each variable to the set for its stage and storage class.
        bool patched = false;
        foreach (int i in Instructions(words))
        {
            if (Opcode(words[i]) == OpDecorate && WordCount(words[i]) >= 4 && words[i + 2] == DecorationDescriptorSet
                && storageClasses.TryGetValue(words[i + 1], out uint storageClass))
            {
                uint? set = (stage, storageClass) switch
                {
                    (ShaderStage.Vertex, StorageClassUniformConstant) => 0,
                    (ShaderStage.Vertex, StorageClassUniform) => 1,
                    (ShaderStage.Fragment, StorageClassUniformConstant) => 2,
                    (ShaderStage.Fragment, StorageClassUniform) => 3,
                    _ => null,
                };
                if (set is uint value)
                {
                    words[i + 3] = value;
                    patched = true;
                }
            }
        }

        // Pass 3: renumber the bindings within each set consecutively from 0, in
        // their original order.  Slang numbers bindings across resource types, so
        // once a stage's uniform buffer and sampler land in different sets the
        // sampler can be left at binding 1 with nothing at 0.
        var bindingWordIndices = new Dictionary<uint, int>();
        var sets = new Dictionary<uint, uint>();
        foreach (int i in Instructions(words))
        {
            if (Opcode(words[i]) == OpDecorate && WordCount(words[i]) >= 4)
            {
                if (words[i + 2] == DecorationBinding)
                {
                    bindingWordIndices[words[i + 1]] = i + 3;
                }
                else if (words[i + 2] == DecorationDescriptorSet)
                {
                    sets[words[i + 1]] = words[i + 3];
                }
            }
        }
        var bindingsBySet = bindingWordIndices
            .Where(binding => sets.ContainsKey(binding.Key))
            .GroupBy(binding => sets[binding.Key], binding => (WordIndex: binding.Value, Binding: words[binding.Value]));
        foreach (var set in bindingsBySet)
        {
            uint newBinding = 0;
            foreach (var (wordIndex, _) in set.OrderBy(b => b.Binding).ThenBy(b => b.WordIndex))
            {
                if (words[wordIndex] != newBinding)
                {
                    words[wordIndex] = newBinding;
                    patched = true;
                }
                newBinding++;
            }
        }

        if (patched)
        {
            Buffer.BlockCopy(words, 0, bytes, 0, words.Length * 4);
            File.WriteAllBytes(spvPath, bytes);
        }
    }

    /// <summary>Yields the word index of each instruction after the header.</summary>
    internal static IEnumerable<int> Instructions(uint[] words)
    {
        int i = HeaderWordCount;
        while (i < words.Length)
        {
            int wordCount = WordCount(words[i]);
            if (wordCount == 0 || i + wordCount > words.Length)
            {
                yield break;
            }
            yield return i;
            i += wordCount;
        }
    }

    internal static uint Opcode(uint word) => word & 0xFFFF;

    internal static int WordCount(uint word) => (int)(word >> 16);
}
