// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _AddIndexMetadata – assigns Index (1-based) and Total metadata to items
// so batched targets can display progress counters such as [1/5].
public sealed class _AddIndexMetadata : Task
{
    [Required]
    public ITaskItem[] InputItems { get; set; }

    [Output]
    public ITaskItem[] OutputItems { get; set; }

    public override bool Execute()
    {
        this.OutputItems = new ITaskItem[this.InputItems.Length];
        for (int i = 0; i < this.InputItems.Length; i++)
        {
            this.OutputItems[i] = new TaskItem(this.InputItems[i]);
            this.OutputItems[i].SetMetadata("Index", (i + 1).ToString());
            this.OutputItems[i].SetMetadata("Total", this.InputItems.Length.ToString());

            // Compute output-relative directory from Link metadata,
            // falling back to RelativeDir.
            string link = this.OutputItems[i].GetMetadata("Link");
            if (!string.IsNullOrEmpty(link))
            {
                int lastSep = link.LastIndexOfAny(new[] { '\\', '/' });
                string dir = lastSep >= 0 ? link.Substring(0, lastSep + 1) : "";
                this.OutputItems[i].SetMetadata("OutputRelativeDir", dir);

                // NOTE: From the name alone, as a link's backslash isn't a separator to Path on Unix
                this.OutputItems[i].SetMetadata("OutputName", Path.GetFileNameWithoutExtension(link.Substring(lastSep + 1)));
            }
            else
            {
                string relativeDir = this.OutputItems[i].GetMetadata("RelativeDir");
                if (Path.IsPathRooted(relativeDir) || relativeDir.StartsWith("..", StringComparison.Ordinal))
                {
                    this.Log.LogError(
                      "Content item '{0}' is outside the project, so it would be written outside the content output folder: give it Link metadata saying where it goes.",
                      this.InputItems[i].ItemSpec);
                }

                this.OutputItems[i].SetMetadata("OutputRelativeDir", relativeDir);
                this.OutputItems[i].SetMetadata("OutputName", this.OutputItems[i].GetMetadata("Filename"));
            }
        }

        return !this.Log.HasLoggedErrors;
    }
}
