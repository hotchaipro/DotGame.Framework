// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _ResolveContentCacheRoot – locates the directory that should host the
// shared content cache.
//
// Walks up from the project directory and returns the nearest ancestor
// containing a solution file (.sln / .slnx), which reproduces
// $(SolutionDir) when a solution is present but does not require MSBuild
// to have been invoked through one.  If no solution is found anywhere up
// the chain, the nearest ancestor that looks like a repository root
// (.git, .gitignore) is used instead, so single-project repositories and
// solution-less layouts still get a cache.
//
// Returning the same directory for both invocation styles is the whole
// point: a cache that moves depending on how the build was started would
// silently re-encode everything, which is exactly the failure this
// replaced.  Returns empty when nothing suitable is found, which simply
// leaves the cache disabled.
public sealed class _ResolveContentCacheRoot : Task
{
    [Required]
    public string StartDirectory { get; set; }

    public string SolutionDirectory { get; set; }

    [Output]
    public string CacheRoot { get; set; }

    public override bool Execute()
    {
        this.CacheRoot = "";

        // A solution-driven build already knows the answer.  ("*Undefined*"
        // is what MSBuild reports for an unset SolutionDir.)
        if (!string.IsNullOrWhiteSpace(this.SolutionDirectory) &&
            this.SolutionDirectory.IndexOf('*') < 0 && Directory.Exists(this.SolutionDirectory))
        {
            this.CacheRoot = Path.GetFullPath(this.SolutionDirectory);
            return true;
        }

        // NOTE: Stops at the repository, so a cache can't land in a folder above it
        var dir = new DirectoryInfo(Path.GetFullPath(this.StartDirectory));
        string repoFallback = null;
        while (dir != null)
        {
            if (Directory.GetFiles(dir.FullName, "*.sln").Length > 0 ||
                Directory.GetFiles(dir.FullName, "*.slnx").Length > 0 ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                this.CacheRoot = dir.FullName;
                return true;
            }

            // Remember the first folder with its own ignore rules, in case nothing better is found
            if (repoFallback == null && File.Exists(Path.Combine(dir.FullName, ".gitignore")))
            {
                repoFallback = dir.FullName;
            }

            dir = dir.Parent;
        }

        if (repoFallback != null)
        {
            this.CacheRoot = repoFallback;
        }

        return !this.Log.HasLoggedErrors;
    }
}
