using System.Text;
using System.Text.RegularExpressions;

namespace LineEndingNormalizer;

/// <summary>
/// Line Ending Normalizer command-line application.
///
/// Features:
/// - Recursively processes files with wildcard include/exclude patterns.
/// - Skips binary files and common VCS/build directories.
/// - Never traverses directory symlinks or junctions.
/// - Handles arbitrarily large files with bounded memory usage.
/// - Preserves detected encoding and BOM state.
/// - Detects BOM-based and BOM-less Unicode and legacy encodings.
/// - Avoids modifying files whose encoding cannot be safely determined.
/// - Detects and normalizes CRLF, LF, CR, NEL, LS, and PS line separators.
/// - Reports mixed or absent line endings as MIXED or NONE.
/// - Rewrites files only when conversion is required.
/// - Preserves all file content except the line-ending changes requested.
/// - Uses atomic replacement and preserves file attributes/timestamps.
/// - Optionally creates a .bak copy before replacement.
/// - Supports normal, -WhatIf, -ValidateOnly, and -DetectOnly modes.
/// - Supports CSV reporting, deterministic output, quiet output, and
///   configurable bounded parallelism.
/// - Supports -FailOnChanges for CI validation.
/// - Reports per-file failures without aborting the entire scan.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Successful completion.
    /// </summary>
    private const int ExitSuccess = 0;

    /// <summary>
    /// Invalid command-line arguments.
    /// </summary>
    private const int ExitInvalidArguments = 1;

    /// <summary>
    /// Base directory does not exist.
    /// </summary>
    private const int ExitDirectoryNotFound = 2;

    /// <summary>
    /// One or more processing/report errors occurred.
    /// </summary>
    private const int ExitProcessingErrors = 3;

    /// <summary>
    /// -FailOnChanges detected files requiring conversion.
    /// </summary>
    private const int ExitChangesNeeded = 4;

    /// <summary>
    /// Serializes console output from parallel processing.
    /// </summary>
    private static readonly Lock ConsoleLock = new();

    /// <summary>
    /// Default maximum degree of parallelism.
    /// </summary>
    private const int DefaultMaxParallelism = 4;

    /// <summary>
    /// Application entry point.
    /// </summary>
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(
            CodePagesEncodingProvider.Instance);

        Options options;

        try
        {
            options = ParseArguments(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.WriteLine();

            PrintUsage();

            return ExitInvalidArguments;
        }

        if (!Directory.Exists(options.BasePath))
        {
            Console.Error.WriteLine(
                "Directory not found: {0}",
                options.BasePath);

            return ExitDirectoryNotFound;
        }

        if (options.DetectOnly)
        {
            // DetectOnly is strictly read-only and emits CSV to stdout.
            return RunDetectOnly(options);
        }

        ProcessingMode mode =
            options.ValidateOnly
                ? ProcessingMode.ValidateOnly
                : options.WhatIf
                    ? ProcessingMode.WhatIf
                    : ProcessingMode.Normalize;

        var statistics = new Statistics();

        Console.WriteLine("Line Ending Normalizer v1.0");
        Console.WriteLine("Base path : {0}", options.BasePath);
        Console.WriteLine(
            "Patterns  : {0}",
            string.Join(", ", options.IncludePatterns.ToArray()));

        if (options.ExcludePatterns.Count > 0)
        {
            Console.WriteLine(
                "Exclude   : {0}",
                string.Join(", ", options.ExcludePatterns.ToArray()));
        }

        Console.WriteLine(
            "Target    : {0}",
            options.TargetLineEnding.ToString().ToUpperInvariant());

        if (mode == ProcessingMode.WhatIf)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                "Mode      : Preview only (no files will be modified)");
            Console.ResetColor();
        }
        else if (mode == ProcessingMode.ValidateOnly)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                "Mode      : Validate only (no files will be modified)");
            Console.ResetColor();
        }

        Console.WriteLine();

        bool reportOk =
            RunConvertOrValidate(
                options,
                statistics,
                mode);

        Console.WriteLine();

        PrintSummary(statistics,
            mode);

        if (!reportOk ||
            statistics.Errors > 0)
        {
            return ExitProcessingErrors;
        }

        if (options.FailOnChanges &&
            statistics.Converted > 0)
        {
            return ExitChangesNeeded;
        }

        return ExitSuccess;
    }


    /// <summary>
    /// Parses command-line arguments.
    ///
    /// Supported:
    ///   -BasePath <path>
    ///   -Include <patterns>
    ///   -Exclude <patterns>
    ///   -Target <CR|LF|CRLF>
    ///   -Verbose
    ///   -Quiet
    ///   -WhatIf
    ///   -ValidateOnly
    ///   -FailOnChanges
    ///   -Backup
    ///   -DetectOnly
    ///   -Deterministic
    ///   -Report <path>
    ///   -MaxParallelism <N>
    ///   -FullPath
    /// </summary>
    internal static Options ParseArguments(
        string[] args)
    {
        var options = new Options();

        for (int i = 0;
             i < args.Length;
             i++)
        {
            string arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "-basepath":

                    if (++i >= args.Length)
                    {
                        throw new ArgumentException(
                            "Missing value for -BasePath.");
                    }

                    options.BasePath = args[i];

                    break;

                case "-include":

                    if (++i >= args.Length)
                    {
                        throw new ArgumentException(
                            "Missing value for -Include.");
                    }

                    foreach (var pattern in SplitPatterns(args[i]))
                    {
                        options.IncludePatterns.Add(pattern);
                    }

                    break;

                case "-exclude":

                    if (++i >= args.Length)
                    {
                        throw new ArgumentException(
                            "Missing value for -Exclude.");
                    }

                    foreach (var pattern in SplitPatterns(args[i]))
                    {
                        options.ExcludePatterns.Add(pattern);
                    }

                    break;

                case "-target":

                    if (++i >= args.Length)
                    {
                        throw new ArgumentException(
                            "Missing value for -Target.");
                    }

                    options.TargetLineEnding =
                        args[i].ToUpperInvariant() switch
                        {
                            "CRLF" or "WINDOWS" => LineEnding.Crlf,
                            "LF" or "UNIX" => LineEnding.Lf,
                            "CR" or "MAC" => LineEnding.Cr,

                            _ => throw new ArgumentException(
                                "Invalid target line ending: " +
                                args[i])
                        };

                    break;

                case "-verbose":

                    options.Verbose = true;

                    break;

                case "-previewonly":

                    throw new ArgumentException(
                        "-PreviewOnly has been renamed to -WhatIf.");

                case "-whatif":

                    options.WhatIf = true;

                    break;

                case "-validateonly":

                    options.ValidateOnly = true;

                    break;

                case "-failonchanges":

                    options.FailOnChanges = true;

                    break;

                case "-quiet":

                    options.Quiet = true;

                    break;

                case "-backup":

                    options.Backup = true;

                    break;

                case "-detectonly":

                    options.DetectOnly = true;

                    break;

                case "-deterministic":

                    options.Deterministic = true;

                    break;

                case "-report":

                    if (++i >= args.Length)
                    {
                        throw new ArgumentException(
                            "Missing value for -Report.");
                    }

                    options.Report = args[i];

                    break;

                case "-fullpath":

                    options.FullPath = true;

                    break;

                case "-maxparallelism":

                    if (++i >= args.Length)
                    {
                        throw new ArgumentException(
                            "Missing value for -MaxParallelism.");
                    }

                    if (!int.TryParse(
                            args[i],
                            out int maxParallelism) ||
                        maxParallelism < 1)
                    {
                        throw new ArgumentException(
                            "Invalid value for -MaxParallelism " +
                            "(must be a positive integer): " +
                            args[i]);
                    }

                    options.MaxParallelism =
                        maxParallelism;

                    break;

                default:

                    throw new ArgumentException(
                        "Unknown argument: " +
                        arg);
            }
        }

        if (string.IsNullOrEmpty(options.BasePath))
        {
            throw new ArgumentException(
                "Base path is required.");
        }

        if (options.IncludePatterns.Count == 0)
        {
            options.IncludePatterns.Add("*");
        }

        if (options is { DetectOnly: true, ValidateOnly: true })
        {
            throw new ArgumentException(
                "-DetectOnly and -ValidateOnly cannot be combined.");
        }

        return options;
    }


    /// <summary>
    /// Splits comma/semicolon-separated patterns and removes empty entries.
    /// </summary>
    private static string[] SplitPatterns(
        string arg)
    {
        string[] patterns =
            arg.Split(
                [
                    ',',
                    ';'
                ],
                StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0;
             i < patterns.Length;
             i++)
        {
            patterns[i] = patterns[i].Trim();
        }

        return patterns;
    }


    /// <summary>
    /// Prints a directory-traversal warning under the console lock, matching
    /// the DarkYellow presentation used elsewhere for non-fatal scan issues.
    /// </summary>
    private static void PrintTraversalWarning(string message)
    {
        lock (ConsoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Error.WriteLine(message);
            Console.ResetColor();
        }
    }


    #region Normalize / WhatIf / ValidateOnly

    /// <summary>
    /// Processing result for one file. The same ResultLabel is used by
    /// console and CSV output.
    /// </summary>
    private sealed record FileOutcome(
        string DisplayPath,
        string SortKey,
        NormalizeResult? Result,
        string ResultLabel,
        bool ConsoleVisible,
        DetectResult? Detected,
        bool IsError,
        string? ErrorMessage);


    /// <summary>
    /// Detects optional report information before processing, then
    /// normalizes, previews, or validates the file.
    /// </summary>
    private static FileOutcome ComputeFileOutcome(
        string file,
        Options options,
        ProcessingMode mode,
        Statistics statistics)
    {
        statistics.IncrementChecked();

        string displayPath =
            FormatDisplayPath(
                file,
                options);

        bool previewOnly =
            mode != ProcessingMode.Normalize;

        bool backup =
            mode == ProcessingMode.Normalize &&
            options.Backup;

        DetectResult? detected = null;

        //
        // Detect before NormalizeFile so a report describes the original
        // file rather than the already-converted file.
        //
        if (options.Report != null)
        {
            try
            {
                detected =
                    NewLineNormalizer.DetectFile(file);
            }
            catch
            {
                // Report detection is best-effort.
            }
        }

        try
        {
            NormalizeResult result =
                NewLineNormalizer.NormalizeFile(
                    file,
                    options.TargetLineEnding,
                    previewOnly,
                    backup);

            switch (result)
            {
                case NormalizeResult.Converted:
                    statistics.IncrementConverted();
                    break;

                case NormalizeResult.Unchanged:
                    statistics.IncrementUnchanged();
                    break;

                case NormalizeResult.EncodingNotDetected:
                    statistics.IncrementSkipped();
                    break;
            }

            return new FileOutcome(
                displayPath,
                file,
                result,
                GetResultLabel(result, mode),
                GetConsoleVisibility(result, options),
                detected,
                IsError: false,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            statistics.IncrementErrors();

            return new FileOutcome(
                displayPath,
                file,
                Result: null,
                ResultLabel: "Error",
                ConsoleVisible: true,
                detected,
                IsError: true,
                ErrorMessage: ex.Message);
        }
    }


    /// <summary>
    /// Maps an operation result to its mode-specific report label.
    /// </summary>
    private static string GetResultLabel(
        NormalizeResult result,
        ProcessingMode mode)
    {
        return (result, mode) switch
        {
            (NormalizeResult.Converted,
                ProcessingMode.Normalize)
                => "Converted",

            (NormalizeResult.Converted,
                ProcessingMode.WhatIf)
                => "Would convert",

            (NormalizeResult.Converted,
                ProcessingMode.ValidateOnly)
                => "Invalid",

            (NormalizeResult.Unchanged,
                ProcessingMode.ValidateOnly)
                => "Already normalized",

            (NormalizeResult.Unchanged, _)
                => "Unchanged",

            (NormalizeResult.EncodingNotDetected, _)
                => "Skipped (unknown encoding)",

            _ => throw new ArgumentOutOfRangeException(
                nameof(result))
        };
    }


    /// <summary>
    /// Determines whether a normal result is shown on the console.
    /// Unchanged files require -Verbose; all other results require
    /// -Quiet to be suppressed.
    /// </summary>
    private static bool GetConsoleVisibility(
        NormalizeResult result,
        Options options)
    {
        return result switch
        {
            NormalizeResult.Unchanged =>
                options is { Verbose: true, Quiet: false },

            _ =>
                !options.Quiet
        };
    }


    /// <summary>
    /// Emits one processing result under the console lock.
    /// </summary>
    private static void EmitConsole(
        FileOutcome outcome,
        ProcessingMode mode)
    {
        if (outcome.IsError)
        {
            lock (ConsoleLock)
            {
                Console.ForegroundColor =
                    ConsoleColor.Red;

                Console.Error.WriteLine(
                    outcome.DisplayPath);

                Console.Error.WriteLine(
                    "    " + outcome.ErrorMessage);

                Console.ResetColor();
            }

            return;
        }

        if (!outcome.ConsoleVisible)
        {
            return;
        }

        lock (ConsoleLock)
        {
            switch (outcome.Result)
            {
                case NormalizeResult.Unchanged:

                    Console.WriteLine(
                        "OK     {0}",
                        outcome.DisplayPath);

                    break;

                case NormalizeResult.EncodingNotDetected:

                    Console.ForegroundColor =
                        ConsoleColor.DarkYellow;

                    Console.WriteLine(
                        "{0}: {1}",
                        outcome.ResultLabel,
                        outcome.DisplayPath);

                    Console.ResetColor();

                    break;

                case NormalizeResult.Converted:

                    Console.ForegroundColor =
                        mode == ProcessingMode.Normalize
                            ? ConsoleColor.Green
                            : ConsoleColor.Yellow;

                    Console.WriteLine(
                        "{0}: {1}",
                        outcome.ResultLabel,
                        outcome.DisplayPath);

                    Console.ResetColor();

                    break;
            }
        }
    }


    /// <summary>
    /// Processes candidate files with bounded parallelism.
    ///
    /// With -Deterministic, files and console output are ordinal-sorted.
    /// Without it, console output is emitted as files complete.
    ///
    /// When -Report is specified, report rows are always ordinal-sorted,
    /// independently of -Deterministic.
    /// </summary>
    private static bool RunConvertOrValidate(
        Options options,
        Statistics statistics,
        ProcessingMode mode)
    {
        var includePatterns =
            FilePatternMatcher.Compile(
                options.IncludePatterns);

        List<Regex>? excludePatterns =
            options.ExcludePatterns.Count > 0
                ? FilePatternMatcher.Compile(
                    options.ExcludePatterns)
                : null;

        var candidateFiles =
            DirectoryTraversal.EnumerateCandidateFiles(
                options.BasePath ?? "",
                onWarning: PrintTraversalWarning)
            .Where(file =>
                DirectoryTraversal.IsCandidateFile(
                    Path.GetFileName(file),
                    includePatterns,
                    excludePatterns));

        bool wantsReport =
            options.Report != null;

        List<FileOutcome>? reportOutcomes =
            wantsReport
                ? []
                : null;

        var reportOutcomesLock = new Lock();

        if (options.Deterministic)
        {
            List<string> files =
                [.. candidateFiles];

            files.Sort(
                StringComparer.Ordinal);

            var outcomes =
                new FileOutcome[files.Count];

            Parallel.For(
                0,
                files.Count,
                CreateParallelOptions(options),
                i =>
                {
                    outcomes[i] =
                        ComputeFileOutcome(
                            files[i],
                            options,
                            mode,
                            statistics);
                });

            foreach (var outcome in outcomes)
            {
                EmitConsole(
                    outcome,
                    mode);
            }

            if (wantsReport)
            {
                reportOutcomes =
                    [.. outcomes];
            }
        }
        else
        {
            Parallel.ForEach(
                candidateFiles,
                CreateParallelOptions(options),
                file =>
                {
                    FileOutcome outcome =
                        ComputeFileOutcome(
                            file,
                            options,
                            mode,
                            statistics);

                    EmitConsole(
                        outcome,
                        mode);

                    if (wantsReport)
                    {
                        lock (reportOutcomesLock)
                        {
                            reportOutcomes!.Add(
                                outcome);
                        }
                    }
                });

            reportOutcomes?.Sort(
                (a, b) =>
                    string.CompareOrdinal(
                        a.SortKey,
                        b.SortKey));
        }

        if (!wantsReport)
        {
            return true;
        }

        List<string> rows =
            [..
                reportOutcomes!.Select(
                    outcome =>
                        BuildNormalizeCsvRow(
                            outcome,
                            options))];

        if (TryWriteReportFile(
                options.Report!,
                "File,Encoding,BOM,LineEnding,Target,Result",
                rows,
                out string? error))
        {
            Console.WriteLine(
                "Report written: {0}",
                Path.GetFullPath(
                    options.Report!));

            return true;
        }

        Console.Error.WriteLine(
            "Error writing report: {0}",
            options.Report);

        Console.Error.WriteLine(
            "    " + error);

        return false;
    }


    /// <summary>
    /// Builds one normal/-WhatIf/-ValidateOnly report row.
    /// Detection columns remain blank when optional detection failed.
    /// </summary>
    private static string BuildNormalizeCsvRow(
        FileOutcome outcome,
        Options options)
    {
        (string encoding,
            string bom,
            string lineEnding) =
            outcome.Detected != null
                ? BuildDetectResultColumns(
                    outcome.Detected)
                : ("", "", "");

        string target =
            options.TargetLineEnding
                .ToString()
                .ToUpperInvariant();

        return string.Join(
            ",",
            EscapeCsvField(outcome.DisplayPath),
            EscapeCsvField(encoding),
            bom,
            lineEnding,
            target,
            EscapeCsvField(outcome.ResultLabel));
    }

    #endregion


    /// <summary>
    /// Converts a detection result into stable report columns.
    /// </summary>
    private static (
        string Encoding,
        string Bom,
        string LineEnding)
        BuildDetectResultColumns(
            DetectResult result)
    {
        return (
            GetEncodingDisplayName(
                result.Encoding),

            result.HasBom
                ? "Yes"
                : "No",

            GetLineEndingKindDisplayName(
                result.LineEndingKind));
    }


    /// <summary>
    /// Creates bounded parallel-processing options.
    /// </summary>
    internal static ParallelOptions CreateParallelOptions(
        Options options)
    {
        int maxDegreeOfParallelism =
            options.MaxParallelism ??
            Math.Max(
                1,
                Math.Min(
                    Environment.ProcessorCount,
                    DefaultMaxParallelism));

        return new ParallelOptions
        {
            MaxDegreeOfParallelism =
                maxDegreeOfParallelism
        };
    }


    #region -DetectOnly

    /// <summary>
    /// Result of scanning one file in -DetectOnly mode.
    /// </summary>
    private sealed record DetectOutcome(
        string DisplayPath,
        string SortKey,
        DetectResult? Detected,
        bool IsError,
        string? Message);


    /// <summary>
    /// Performs read-only detection for one file.
    /// </summary>
    private static DetectOutcome ComputeDetectOutcome(
        string file,
        Options options)
    {
        string displayPath =
            FormatDisplayPath(
                file,
                options);

        try
        {
            DetectResult? result =
                NewLineNormalizer.DetectFile(
                    file);

            return new DetectOutcome(
                displayPath,
                file,
                result,
                IsError: false,
                Message: null);
        }
        catch (Exception ex)
        {
            return new DetectOutcome(
                displayPath,
                file,
                Detected: null,
                IsError: true,
                Message: ex.Message);
        }
    }


    /// <summary>
    /// Builds one -DetectOnly CSV row.
    /// </summary>
    private static string BuildDetectCsvRow(
        string displayPath,
        DetectResult result)
    {
        (string encoding,
            string bom,
            string lineEnding) =
            BuildDetectResultColumns(
                result);

        return string.Join(
            ",",
            lineEnding,
            encoding,
            bom,
            EscapeCsvField(displayPath));
    }


    /// <summary>
    /// Emits one DetectOnly result.
    /// </summary>
    private static void EmitDetectConsole(
        DetectOutcome outcome)
    {
        if (outcome.IsError)
        {
            lock (ConsoleLock)
            {
                Console.Error.WriteLine(
                    "Error scanning: {0}",
                    outcome.DisplayPath);

                Console.Error.WriteLine(
                    "    " + outcome.Message);
            }

            return;
        }

        if (outcome.Detected == null)
        {
            lock (ConsoleLock)
            {
                Console.Error.WriteLine(
                    "Skipped (unknown encoding): {0}",
                    outcome.DisplayPath);
            }

            return;
        }

        lock (ConsoleLock)
        {
            Console.WriteLine(
                BuildDetectCsvRow(
                    outcome.DisplayPath,
                    outcome.Detected));
        }
    }


    /// <summary>
    /// Runs the strictly read-only -DetectOnly pipeline.
    /// Stdout contains CSV; diagnostics are written to stderr.
    /// </summary>
    private static int RunDetectOnly(
        Options options)
    {
        var includePatterns =
            FilePatternMatcher.Compile(
                options.IncludePatterns);

        List<Regex>? excludePatterns =
            options.ExcludePatterns.Count > 0
                ? FilePatternMatcher.Compile(
                    options.ExcludePatterns)
                : null;

        var candidateFiles =
            DirectoryTraversal.EnumerateCandidateFiles(
                options.BasePath ?? "",
                onWarning: PrintTraversalWarning)
            .Where(file =>
                DirectoryTraversal.IsCandidateFile(
                    Path.GetFileName(file),
                    includePatterns,
                    excludePatterns));

        Console.WriteLine(
            "LineEnding,Encoding,BOM,File");

        bool wantsReport =
            options.Report != null;

        List<DetectOutcome>? reportOutcomes =
            wantsReport
                ? []
                : null;

        var reportOutcomesLock = new Lock();

        int errorCount = 0;

        if (options.Deterministic)
        {
            List<string> files =
                [.. candidateFiles];

            files.Sort(
                StringComparer.Ordinal);

            var outcomes =
                new DetectOutcome[files.Count];

            Parallel.For(
                0,
                files.Count,
                CreateParallelOptions(options),
                i =>
                {
                    outcomes[i] =
                        ComputeDetectOutcome(
                            files[i],
                            options);
                });

            foreach (var outcome in outcomes)
            {
                if (outcome.IsError)
                {
                    errorCount++;
                }

                EmitDetectConsole(
                    outcome);
            }

            if (wantsReport)
            {
                reportOutcomes =
                    [.. outcomes];
            }
        }
        else
        {
            Parallel.ForEach(
                candidateFiles,
                CreateParallelOptions(options),
                file =>
                {
                    DetectOutcome outcome =
                        ComputeDetectOutcome(
                            file,
                            options);

                    if (outcome.IsError)
                    {
                        Interlocked.Increment(
                            ref errorCount);
                    }

                    EmitDetectConsole(
                        outcome);

                    if (wantsReport)
                    {
                        lock (reportOutcomesLock)
                        {
                            reportOutcomes!.Add(
                                outcome);
                        }
                    }
                });

            reportOutcomes?.Sort(
                (a, b) =>
                    string.CompareOrdinal(
                        a.SortKey,
                        b.SortKey));
        }

        if (wantsReport)
        {
            List<string> rows =
                [..
                    reportOutcomes!
                        .Where(o =>
                            o.Detected != null)
                        .Select(o =>
                            BuildDetectCsvRow(
                                o.DisplayPath,
                                o.Detected!))];

            if (TryWriteReportFile(
                    options.Report!,
                    "LineEnding,Encoding,BOM,File",
                    rows,
                    out string? error))
            {
                Console.Error.WriteLine(
                    "Report written: {0}",
                    Path.GetFullPath(
                        options.Report!));
            }
            else
            {
                Console.Error.WriteLine(
                    "Error writing report: {0}",
                    options.Report);

                Console.Error.WriteLine(
                    "    " + error);

                return ExitProcessingErrors;
            }
        }

        return errorCount == 0
            ? ExitSuccess
            : ExitProcessingErrors;
    }

    #endregion


    /// <summary>
    /// Writes a UTF-8 CSV report with CRLF row terminators.
    /// </summary>
    private static bool TryWriteReportFile(
        string path,
        string header,
        IReadOnlyList<string> rows,
        out string? errorMessage)
    {
        try
        {
            using var writer =
                new StreamWriter(
                    path,
                    append: false,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));

            writer.NewLine = "\r\n";

            writer.WriteLine(header);

            foreach (var row in rows)
            {
                writer.WriteLine(row);
            }

            errorMessage = null;

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            errorMessage = ex.Message;

            return false;
        }
    }


    /// <summary>
    /// Maps line-ending kinds to stable CSV names.
    /// </summary>
    internal static string GetLineEndingKindDisplayName(
        LineEndingKind kind)
    {
        return kind switch
        {
            LineEndingKind.Crlf => "CRLF",
            LineEndingKind.Lf => "LF",
            LineEndingKind.Cr => "CR",
            LineEndingKind.Mixed => "MIXED",
            LineEndingKind.None => "NONE",

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind))
        };
    }


    /// <summary>
    /// Maps detected encodings to stable display names.
    /// </summary>
    internal static string GetEncodingDisplayName(
        Encoding encoding)
    {
        if (encoding.Equals(
                UnicodeDetector.Utf8Bom) ||
            encoding.Equals(
                UnicodeDetector.Utf8NoBom))
        {
            return "UTF-8";
        }

        if (encoding.Equals(
                UnicodeDetector.Utf16LittleEndianBom) ||
            encoding.Equals(
                UnicodeDetector.Utf16LittleEndianNoBom))
        {
            return "UTF-16LE";
        }

        if (encoding.Equals(
                UnicodeDetector.Utf16BigEndianBom) ||
            encoding.Equals(
                UnicodeDetector.Utf16BigEndianNoBom))
        {
            return "UTF-16BE";
        }

        if (encoding.Equals(
                UnicodeDetector.Utf32LittleEndianBom) ||
            encoding.Equals(
                UnicodeDetector.Utf32LittleEndianNoBom))
        {
            return "UTF-32LE";
        }

        if (encoding.Equals(
                UnicodeDetector.Utf32BigEndianBom) ||
            encoding.Equals(
                UnicodeDetector.Utf32BigEndianNoBom))
        {
            return "UTF-32BE";
        }

        if (Equals(
                encoding,
                Encoding.ASCII))
        {
            return "ASCII";
        }

        return encoding.WebName;
    }


    /// <summary>
    /// Escapes a CSV field according to RFC 4180.
    /// </summary>
    internal static string EscapeCsvField(
        string field)
    {
        if (field.IndexOfAny(
                [
                    ',',
                    '"',
                    '\r',
                    '\n'
                ]) < 0)
        {
            return field;
        }

        return "\"" +
               field.Replace(
                   "\"",
                   "\"\"") +
               "\"";
    }


    /// <summary>
    /// Formats a path for output. File operations continue to use
    /// the original path.
    /// </summary>
    internal static string FormatDisplayPath(
        string file,
        Options options)
    {
        if (options.FullPath)
        {
            return Path.GetFullPath(file);
        }

        return Path.GetRelativePath(
            options.BasePath ?? file,
            file);
    }


    /// <summary>
    /// Prints final processing statistics.
    /// </summary>
    private static void PrintSummary(
        Statistics statistics,
        ProcessingMode mode)
    {
        Console.ForegroundColor =
            ConsoleColor.Cyan;

        Console.WriteLine(
            mode == ProcessingMode.ValidateOnly
                ? "Validation complete."
                : "Scan complete.");

        Console.ResetColor();

        Console.WriteLine(
            "{0,-19}: {1,8}",
            "Files scanned",
            statistics.Checked);

        if (mode == ProcessingMode.ValidateOnly)
        {
            Console.WriteLine(
                "{0,-19}: {1,8}",
                "Already normalized",
                statistics.Unchanged);

            Console.ForegroundColor =
                ConsoleColor.Yellow;

            Console.WriteLine(
                "{0,-19}: {1,8}",
                "Invalid",
                statistics.Converted);

            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor =
                mode == ProcessingMode.WhatIf
                    ? ConsoleColor.Yellow
                    : ConsoleColor.Green;

            Console.WriteLine(
                "{0,-19}: {1,8}",
                mode == ProcessingMode.WhatIf
                    ? "Would convert"
                    : "Converted",
                statistics.Converted);

            Console.ResetColor();

            Console.WriteLine(
                "{0,-19}: {1,8}",
                "Already normalized",
                statistics.Unchanged);
        }

        Console.ForegroundColor =
            ConsoleColor.DarkYellow;

        Console.WriteLine(
            "{0,-19}: {1,8}",
            "Skipped",
            statistics.Skipped);

        Console.ResetColor();

        Console.WriteLine(
            "{0,-19}: {1,8}",
            "Failed",
            statistics.Errors);
    }


    /// <summary>
    /// Displays command-line usage.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            LineEndingNormalizer v1.0

            Usage:

              LineEndingNormalizer.exe
                  -BasePath <directory>
                  [-Include "<pattern1,pattern2,...>"]
                  [-Exclude "<pattern1,pattern2,...>"]
                       Wildcard file-name patterns. Exclusions are applied
                       after -Include. The following directories are always
                       skipped: .git, .svn, .hg, .vs, .idea, bin, obj,
                       node_modules, packages, dist, build, target.

                  Normalization:
                  [-Target <CRLF|LF|CR>]
                       Target line-ending style. WINDOWS/UNIX/MAC are
                       accepted as aliases. Default: CRLF.

                  Modes:
                       Normalization is the default mode.
                  [-ValidateOnly]
                       Validate without writing anything. Files requiring
                       conversion are reported as Invalid. May be combined
                       with -FailOnChanges. Cannot be combined with
                       -DetectOnly.
                  [-DetectOnly]
                       Read-only detection mode. Writes
                       LineEnding,Encoding,BOM,File CSV rows to stdout.
                       Nothing is modified. -Target, -WhatIf, -Backup,
                       -FailOnChanges, -Quiet, and -Verbose have no effect.
                       Cannot be combined with -ValidateOnly.

                  Report:
                  [-Report <path>]
                       Write a CSV report in addition to normal console
                       output. Valid in every mode. Detection data is
                       collected before normalization so the report
                       describes the original file.

                  Performance:
                  [-MaxParallelism <N>]
                       Maximum number of files processed concurrently.
                       Default: min(logical processor count, 4).

                  Dry run / safety:
                  [-WhatIf]
                       Convert mode only. Computes what each file's result
                       would be without writing anything.
                  [-Backup]
                       Convert mode only (ignored under -WhatIf). Before
                       overwriting a file, copies the original to
                       "<file>.bak" (overwriting any previous one).

                  Output:
                  [-Verbose]
                       Show every checked file, including unchanged files.
                  [-Quiet]
                       Suppress per-file console output. Summary and errors
                       remain visible. Does not affect -Report.
                  [-FullPath]
                       Use absolute paths instead of paths relative to
                       -BasePath. Applies equally to console and CSV output.
                  [-Deterministic]
                       Process and emit console results in ordinal lexical
                       path order. Report rows are always sorted.

                  CI / Exit Code:
                  [-FailOnChanges]
                       Exit with a non-zero code when any file requires (or,
                       under -Validate, fails) conversion. Useful as a CI
                       gate, optionally with -WhatIf or -Validate.

            Examples:

              LineEndingNormalizer.exe ^
                  -BasePath C:\Source ^
                  -Include "*.cs,*.txt" ^
                  -Target CRLF

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*.cpp,*.hpp" ^
                  -Target LF ^
                  -WhatIf

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*.cs" ^
                  -Exclude "*.designer.cs,*.g.cs" ^
                  -Target CRLF

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*" ^
                  -Target CRLF ^
                  -WhatIf ^
                  -FailOnChanges ^
                  -Quiet

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*.cs,*.txt" ^
                  -Target CRLF ^
                  -Backup

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*" ^
                  -DetectOnly ^
                  > report.csv

              LineEndingNormalizer.exe ^
                  -BasePath D:\NetworkShare ^
                  -Include "*.txt" ^
                  -Target CRLF ^
                  -MaxParallelism 2

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*" ^
                  -DetectOnly ^
                  -FullPath ^
                  > report.csv

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*" ^
                  -Target CRLF ^
                  -ValidateOnly ^
                  -FailOnChanges ^
                  -Quiet

              LineEndingNormalizer.exe ^
                  -BasePath . ^
                  -Include "*" ^
                  -Target CRLF ^
                  -Report report.csv ^
                  -Deterministic

            """);
    }
}
