namespace LineEndingNormalizer.Tests;

/// <summary>
/// LEN's exit codes are a published CLI contract, and codes 0-4 are deliberately
/// identical to EncodingChecker's so a script driving both tools can share one
/// mapping. Codes 5 and 6 refine cases EncodingChecker reports as 1, so no code
/// means two different things across the two tools.
///
/// These tests pin the numbers themselves, not just "non-zero" - renumbering is a
/// breaking change for CI gates and must not happen silently.
///
/// Redirects the process-global Console.Out/Error, so they rely on
/// AssemblyInfo.cs's [assembly: CollectionBehavior(DisableTestParallelization = true)]
/// like the other end-to-end tests.
/// </summary>
public sealed class ExitCodeContractTests
{
    private const int ExpectedSuccess = 0;
    private const int ExpectedInvalidArguments = 1;
    private const int ExpectedChangesNeeded = 2;
    private const int ExpectedProcessingErrors = 3;
    private const int ExpectedDirectoryNotFound = 5;
    private const int ExpectedReparsePointRoot = 6;

    private static int RunMain(params string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;

        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(new StringWriter());

            return Program.Main(args);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static byte[] Lf(string text) =>
        System.Text.Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n"));

    [Fact]
    public void Help_ExitsZero()
    {
        Assert.Equal(ExpectedSuccess, RunMain("-?"));
    }

    [Fact]
    public void CleanRun_ExitsZero()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", Lf("alpha\nbeta\n"));

        Assert.Equal(
            ExpectedSuccess,
            RunMain("-BasePath", dir.Path, "-Include", "*.txt", "-Target", "CRLF"));
    }

    [Fact]
    public void UnknownArgument_ExitsOne()
    {
        using var dir = new TempDirectory();

        Assert.Equal(
            ExpectedInvalidArguments,
            RunMain("-BasePath", dir.Path, "-NoSuchSwitch"));
    }

    [Fact]
    public void MissingArgumentValue_ExitsOne()
    {
        Assert.Equal(ExpectedInvalidArguments, RunMain("-BasePath"));
    }

    [Fact]
    public void FailOnChanges_WithFilesNeedingConversion_ExitsTwo()
    {
        // The CI-gate code, and the one most likely to be scripted: it must stay 2,
        // matching EncodingChecker's -FailOnChanges.
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", Lf("alpha\nbeta\n"));

        Assert.Equal(
            ExpectedChangesNeeded,
            RunMain(
                "-BasePath", dir.Path,
                "-Include", "*.txt",
                "-Target", "CRLF",
                "-WhatIf",
                "-FailOnChanges"));
    }

    [Fact]
    public void FailOnChanges_WithNothingToConvert_ExitsZero()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", System.Text.Encoding.ASCII.GetBytes("alpha\r\nbeta\r\n"));

        Assert.Equal(
            ExpectedSuccess,
            RunMain(
                "-BasePath", dir.Path,
                "-Include", "*.txt",
                "-Target", "CRLF",
                "-FailOnChanges"));
    }

    [Fact]
    public void UnwritableReportPath_ExitsThree()
    {
        // A report that cannot be written is a processing failure, not a usage error:
        // by this point the files have already been converted.
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", Lf("alpha\nbeta\n"));

        // A directory occupying the report path makes the write fail deterministically.
        string reportPath = dir.CombinePath("report.csv");
        Directory.CreateDirectory(reportPath);

        Assert.Equal(
            ExpectedProcessingErrors,
            RunMain(
                "-BasePath", dir.Path,
                "-Include", "*.txt",
                "-Target", "CRLF",
                "-Report", reportPath));
    }

    [Fact]
    public void MissingBaseDirectory_ExitsFive()
    {
        using var dir = new TempDirectory();
        string missing = dir.CombinePath("no-such-directory");

        Assert.Equal(
            ExpectedDirectoryNotFound,
            RunMain("-BasePath", missing, "-Include", "*.txt", "-Target", "CRLF"));
    }

    [Fact]
    public void ReparsePointBasePath_ExitsSix()
    {
        using var dir = new TempDirectory();

        string target = dir.CombinePath("real");
        Directory.CreateDirectory(target);

        string junction = dir.CombinePath("link");

        if (!TryCreateJunction(junction, target))
        {
            // Creating a junction can require privileges the test host lacks; skipping
            // is better than asserting something weaker and calling it coverage.
            return;
        }

        Assert.Equal(
            ExpectedReparsePointRoot,
            RunMain("-BasePath", junction, "-Include", "*.txt", "-Target", "CRLF"));
    }

    [Fact]
    public void EveryExitCodeIsDistinct()
    {
        int[] codes =
        [
            ExpectedSuccess,
            ExpectedInvalidArguments,
            ExpectedChangesNeeded,
            ExpectedProcessingErrors,
            4, // cancelled; exercised by CancellationTests rather than through Main
            ExpectedDirectoryNotFound,
            ExpectedReparsePointRoot,
        ];

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();

            return process.ExitCode == 0 && Directory.Exists(junction);
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
