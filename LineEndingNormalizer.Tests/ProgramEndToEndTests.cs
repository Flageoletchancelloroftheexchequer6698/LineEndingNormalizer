using System.Text;

namespace LineEndingNormalizer.Tests;

/// <summary>
/// End-to-end coverage for Program.Main: -Deterministic ordering, -Report
/// CSV content, and -ValidateOnly behavior/exit codes. Redirects the
/// process-global Console.Out/Error around each call, so these tests must
/// run sequentially with the rest of the suite -- see
/// AssemblyInfo.cs's [assembly: CollectionBehavior(DisableTestParallelization = true)].
/// </summary>
public sealed class ProgramEndToEndTests
{
    private static int RunMain(string[] args, out string stdout, out string stderr)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;

        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);

            return Program.Main(args);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);

            stdout = outWriter.ToString();
            stderr = errorWriter.ToString();
        }
    }

    [Fact]
    public void Deterministic_EmitsFilesInOrdinalLexicalOrder()
    {
        using var dir = new TempDirectory();

        // Names chosen so ordinal order differs from creation order.
        string[] names = ["mango.txt", "apple.txt", "zebra.txt", "banana.txt", "kiwi.txt", "fig.txt"];

        foreach (string name in names)
        {
            dir.WriteFile(name, Encoding.ASCII.GetBytes("line one\nline two\n"));
        }

        int exitCode = RunMain(
            ["-BasePath", dir.Path, "-Target", "CRLF", "-WhatIf", "-Deterministic"],
            out string stdout,
            out _);

        Assert.Equal(0, exitCode);

        List<string> emittedOrder =
            [.. names.OrderBy(n => n, StringComparer.Ordinal)];

        int lastIndex = -1;

        foreach (string name in emittedOrder)
        {
            int index = stdout.IndexOf("Would convert: " + name, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find 'Would convert: {name}' in output.");
            Assert.True(index > lastIndex, $"'{name}' appeared out of ordinal order.");
            lastIndex = index;
        }
    }

    [Fact]
    public void Report_WritesCsvWithExpectedSchema()
    {
        using var dir = new TempDirectory();

        dir.WriteFile("already.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
        dir.WriteFile("needsconvert.txt", Encoding.ASCII.GetBytes("a\nb\n"));

        string reportPath = dir.CombinePath("report.csv");

        int exitCode = RunMain(
            ["-BasePath", dir.Path, "-Target", "CRLF", "-WhatIf", "-Report", reportPath, "-Quiet"],
            out _,
            out _);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(reportPath));

        string[] lines = File.ReadAllLines(reportPath);

        Assert.Equal("File,Encoding,BOM,LineEnding,Target,Result", lines[0]);

        string? alreadyRow = lines.FirstOrDefault(l => l.StartsWith("already.txt,", StringComparison.Ordinal));
        string? needsConvertRow = lines.FirstOrDefault(l => l.StartsWith("needsconvert.txt,", StringComparison.Ordinal));

        Assert.NotNull(alreadyRow);
        Assert.NotNull(needsConvertRow);

        Assert.Equal("already.txt,ASCII,No,CRLF,CRLF,Unchanged", alreadyRow);
        Assert.Equal("needsconvert.txt,ASCII,No,LF,CRLF,Would convert", needsConvertRow);
    }

    [Fact]
    public void Report_RowCountUnaffectedByQuietOrVerbose()
    {
        using var dir = new TempDirectory();
        using var reportDir = new TempDirectory();

        dir.WriteFile("a.txt", Encoding.ASCII.GetBytes("x\r\n"));
        dir.WriteFile("b.txt", Encoding.ASCII.GetBytes("x\n"));

        // Written outside the scanned directory: a report file dropped
        // inside it would otherwise be picked up as an extra candidate by
        // the second run, since -Include defaults to "*".
        string quietReport = reportDir.CombinePath("quiet.csv");
        string verboseReport = reportDir.CombinePath("verbose.csv");

        RunMain(["-BasePath", dir.Path, "-Target", "CRLF", "-WhatIf", "-Quiet", "-Report", quietReport], out _, out _);
        RunMain(["-BasePath", dir.Path, "-Target", "CRLF", "-WhatIf", "-Verbose", "-Report", verboseReport], out _, out _);

        // Header + 2 data rows either way.
        Assert.Equal(3, File.ReadAllLines(quietReport).Length);
        Assert.Equal(3, File.ReadAllLines(verboseReport).Length);
    }

    [Fact]
    public void ValidateOnly_HidesAlreadyNormalizedByDefault_ShowsUnderVerbose()
    {
        using var dir = new TempDirectory();

        dir.WriteFile("normalized.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
        dir.WriteFile("invalid.txt", Encoding.ASCII.GetBytes("a\nb\n"));

        RunMain(
            ["-BasePath", dir.Path, "-Target", "CRLF", "-ValidateOnly"],
            out string defaultStdout,
            out _);

        Assert.Contains("Invalid: invalid.txt", defaultStdout);
        Assert.DoesNotContain("normalized.txt", defaultStdout);

        RunMain(
            ["-BasePath", dir.Path, "-Target", "CRLF", "-ValidateOnly", "-Verbose"],
            out string verboseStdout,
            out _);

        Assert.Contains("Invalid: invalid.txt", verboseStdout);
        Assert.Contains("OK     normalized.txt", verboseStdout);
    }

    [Fact]
    public void ValidateOnly_NeverModifiesFiles()
    {
        using var dir = new TempDirectory();

        byte[] originalBytes = Encoding.ASCII.GetBytes("a\nb\n");
        string path = dir.WriteFile("invalid.txt", originalBytes);

        RunMain(["-BasePath", dir.Path, "-Target", "CRLF", "-ValidateOnly", "-Backup"], out _, out _);

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void ValidateOnly_FailOnChanges_ExitsChangesNeeded_WhenInvalidFilesExist()
    {
        using var dir = new TempDirectory();

        dir.WriteFile("invalid.txt", Encoding.ASCII.GetBytes("a\nb\n"));

        int exitCode = RunMain(
            ["-BasePath", dir.Path, "-Target", "CRLF", "-ValidateOnly", "-FailOnChanges", "-Quiet"],
            out _,
            out _);

        Assert.Equal(4, exitCode);
    }

    [Fact]
    public void ValidateOnly_FailOnChanges_ExitsSuccess_WhenAllNormalized()
    {
        using var dir = new TempDirectory();

        dir.WriteFile("normalized.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));

        int exitCode = RunMain(
            ["-BasePath", dir.Path, "-Target", "CRLF", "-ValidateOnly", "-FailOnChanges", "-Quiet"],
            out _,
            out _);

        Assert.Equal(0, exitCode);
    }
}
