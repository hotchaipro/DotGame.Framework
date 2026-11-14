// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _ResolveProcessorCommand – replaces {InputFile}, {OutputFile},
// {IntermediateDir}, {Quality}, and {PremultiplyAlpha}
// tokens in a processor command template string.
public sealed class _ResolveProcessorCommand : Task
{
    [Required]
    public string Template { get; set; }

    [Required]
    public string InputFile { get; set; }

    [Required]
    public string OutputFile { get; set; }

    public string IntermediateDir { get; set; }

    public string CacheDir { get; set; }

    public string Quality { get; set; }

    public string PremultiplyAlpha { get; set; }

    [Output]
    public string ResolvedCommand { get; set; }

    public override bool Execute()
    {
        this.ResolvedCommand = this.Template
          .Replace("{InputFile}", this.InputFile)
          .Replace("{OutputFile}", this.OutputFile)
          .Replace("{IntermediateDir}", (this.IntermediateDir ?? "").TrimEnd('\\', '/'))
          .Replace("{Quality}", this.Quality ?? "")
          .Replace("{PremultiplyAlpha}", string.IsNullOrEmpty(this.PremultiplyAlpha) ? "true" : this.PremultiplyAlpha);

        return !this.Log.HasLoggedErrors;
    }
}
