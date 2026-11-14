// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _WriteSettingsStamps - writes each item's resolved settings to its stamp file, an input of the
// target that encodes it, so a changed setting re-encodes that item and nothing else.
//
// A no-op build runs this for every item of a type. So it compares the whole set with what the last
// build wrote, in one file read, and looks at the stamps themselves only when that differs.
public sealed class _WriteSettingsStamps : Task
{
    [Required]
    public ITaskItem[] Items { get; set; }

    // The metadata naming each item's stamp file, and its settings (semicolons separate lines)
    [Required]
    public string StampFileMetadata { get; set; }

    [Required]
    public string SettingsMetadata { get; set; }

    [Required]
    public string SummaryFile { get; set; }

    public override bool Execute()
    {
        var summary = new StringBuilder();
        foreach (var item in this.Items)
        {
            summary.Append(item.GetMetadata(this.StampFileMetadata)).Append('\t').Append(item.GetMetadata(this.SettingsMetadata)).Append('\n');
        }

        string text = summary.ToString();
        if (File.Exists(this.SummaryFile) && (File.ReadAllText(this.SummaryFile) == text))
        {
            return true;
        }

        foreach (var item in this.Items)
        {
            string stamp = item.GetMetadata(this.StampFileMetadata);
            string settings = string.Join(Environment.NewLine, item.GetMetadata(this.SettingsMetadata).Split(';')) + Environment.NewLine;
            if (!File.Exists(stamp) || (File.ReadAllText(stamp) != settings))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stamp)));
                File.WriteAllText(stamp, settings);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.SummaryFile)));
        File.WriteAllText(this.SummaryFile, text);
        return true;
    }
}
