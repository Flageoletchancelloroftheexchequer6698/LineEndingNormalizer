using System.Text.RegularExpressions;

namespace LineEndingNormalizer;

/// <summary>
/// Directory-tree walking and file-pattern matching used by the CLI's scan pipelines.
/// Reports enumeration problems via an optional <c>onWarning</c> callback rather than
/// writing to the console directly, so callers own console formatting/locking.
/// </summary>
internal static class DirectoryTraversal
{
    /// <summary>
    /// Directories always excluded from recursive scanning.
    /// Matching is by exact directory name, case-insensitive.
    /// </summary>
    private static readonly HashSet<string> DefaultExcludedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".svn",
            ".hg",
            ".vs",
            ".idea",
            "bin",
            "obj",
            "node_modules",
            "packages",
            "dist",
            "build",
            "target"
        };

    /// <summary>
    /// Recursively enumerates candidate files while skipping excluded
    /// directories and directory reparse points.
    /// Enumeration errors are reported via <paramref name="onWarning"/> and skipped.
    /// </summary>
    internal static IEnumerable<string> EnumerateCandidateFiles(
        string basePath,
        Action<string>? onWarning = null,
        string? excludedFullPath = null)
    {
        var pending = new Stack<string>();

        pending.Push(basePath);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

            List<string>? subDirectories =
                TryEnumerate(
                    dir,
                    Directory.EnumerateDirectories,
                    onWarning);

            if (subDirectories != null)
            {
                foreach (var subDirectory in subDirectories)
                {
                    if (DefaultExcludedDirectoryNames.Contains(
                            Path.GetFileName(subDirectory)))
                    {
                        continue;
                    }

                    if (IsReparsePointDirectory(subDirectory))
                    {
                        onWarning?.Invoke(
                            string.Format(
                                "Skipping directory (symlink/junction): {0}",
                                subDirectory));

                        continue;
                    }

                    pending.Push(subDirectory);
                }
            }

            List<string>? files =
                TryEnumerate(
                    dir,
                    Directory.EnumerateFiles,
                    onWarning);

            if (files != null)
            {
                foreach (var file in files)
                {
                    if (excludedFullPath is not null &&
                        string.Equals(file, excludedFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }
    }


    /// <summary>
    /// Returns true for symlink, junction, or other reparse-point
    /// directories. Attribute-read failures are treated conservatively.
    /// </summary>
    internal static bool IsReparsePointDirectory(
        string dir)
    {
        try
        {
            return
                (File.GetAttributes(dir) &
                 FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException)
        {
            return true;
        }
    }


    /// <summary>
    /// Enumerates a directory while recovering from access and I/O errors.
    /// </summary>
    internal static List<string>? TryEnumerate(
        string dir,
        Func<string, IEnumerable<string>> enumerate,
        Action<string>? onWarning = null)
    {
        try
        {
            return [.. enumerate(dir)];
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            IOException)
        {
            onWarning?.Invoke(
                string.Format(
                    "Skipping directory (cannot list): {0}{1}    {2}",
                    dir,
                    Environment.NewLine,
                    ex.Message));

            return null;
        }
    }


    /// <summary>
    /// Determines whether a file matches the include/exclude rules. .bak files are always
    /// excluded. <paramref name="fileName"/> may be a bare filename or a path relative to
    /// the scan root - callers pass whichever FilePatternMatcher's patterns expect to
    /// match against; the ".bak" suffix check works identically either way.
    /// </summary>
    internal static bool IsCandidateFile(
        string fileName,
        List<Regex> includePatterns,
        List<Regex>? excludePatterns)
    {
        //
        // Prevent -Backup with a broad include such as "*" from
        // recursively processing its own backups.
        //
        if (fileName.EndsWith(
                ".bak",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!FilePatternMatcher.IsMatch(
                fileName,
                includePatterns))
        {
            return false;
        }

        if (excludePatterns != null &&
            FilePatternMatcher.IsMatch(
                fileName,
                excludePatterns))
        {
            return false;
        }

        return true;
    }
}
