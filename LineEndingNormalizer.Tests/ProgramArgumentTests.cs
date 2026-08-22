namespace LineEndingNormalizer.Tests;

/// <summary>
/// Coverage for command-line argument parsing.
/// </summary>
public sealed class ProgramArgumentTests
{
    [Fact]
    public void MissingBasePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArguments([]));
    }

    [Fact]
    public void UnknownArgument_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-NotARealFlag"]));
    }

    [Fact]
    public void NoIncludeGiven_DefaultsToMatchAll()
    {
        Options options = Program.ParseArguments(["-BasePath", "."]);

        Assert.Single(options.IncludePatterns);
        Assert.Equal("*", options.IncludePatterns[0]);
        Assert.Empty(options.ExcludePatterns);
    }

    [Fact]
    public void IncludeAndExclude_AreSplitAndTrimmed()
    {
        Options options = Program.ParseArguments(
            ["-BasePath", ".", "-Include", " *.cs , *.txt ", "-Exclude", "*.g.cs,*.designer.cs"]);

        Assert.Equal(["*.cs", "*.txt"], options.IncludePatterns);
        Assert.Equal(["*.g.cs", "*.designer.cs"], options.ExcludePatterns);
    }

    [Theory]
    [InlineData("Crlf", "Crlf")]
    [InlineData("windows", "Crlf")]
    [InlineData("Lf", "Lf")]
    [InlineData("unix", "Lf")]
    [InlineData("Cr", "Cr")]
    [InlineData("mac", "Cr")]
    public void TargetAliases_ResolveToExpectedLineEnding(string arg, string expectedName)
    {
        // LineEnding is internal, so both sides are threaded through by
        // name to keep this [Theory] method's signature public-safe.
        Options options = Program.ParseArguments(["-BasePath", ".", "-Target", arg]);

        Assert.Equal(Enum.Parse<LineEnding>(expectedName), options.TargetLineEnding);
    }

    [Fact]
    public void InvalidTarget_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-Target", "utf-32"]));
    }

    [Fact]
    public void VerboseQuietWhatIfFailOnChanges_AreIndependentFlags()
    {
        Options options = Program.ParseArguments(
            ["-BasePath", ".", "-Verbose", "-Quiet", "-WhatIf", "-FailOnChanges"]);

        Assert.True(options.Verbose);
        Assert.True(options.Quiet);
        Assert.True(options.WhatIf);
        Assert.True(options.FailOnChanges);
    }

    [Fact]
    public void FlagsDefaultToFalse()
    {
        Options options = Program.ParseArguments(["-BasePath", "."]);

        Assert.False(options.Verbose);
        Assert.False(options.Quiet);
        Assert.False(options.WhatIf);
        Assert.False(options.FailOnChanges);
        Assert.False(options.Deterministic);
        Assert.False(options.ValidateOnly);
        Assert.Null(options.Report);
    }

    [Fact]
    public void PreviewOnly_NoLongerAccepted_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-PreviewOnly"]));
    }

    [Fact]
    public void WhatIf_SetsOption()
    {
        Options options = Program.ParseArguments(["-BasePath", ".", "-WhatIf"]);

        Assert.True(options.WhatIf);
    }

    [Fact]
    public void Deterministic_SetsOption()
    {
        Options options = Program.ParseArguments(["-BasePath", ".", "-Deterministic"]);

        Assert.True(options.Deterministic);
    }

    [Fact]
    public void ValidateOnly_SetsOption()
    {
        Options options = Program.ParseArguments(["-BasePath", ".", "-ValidateOnly"]);

        Assert.True(options.ValidateOnly);
    }

    [Fact]
    public void Report_ParsesPathValue()
    {
        Options options = Program.ParseArguments(["-BasePath", ".", "-Report", "out.csv"]);

        Assert.Equal("out.csv", options.Report);
    }

    [Fact]
    public void Report_MissingValue_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-Report"]));
    }

    [Fact]
    public void DetectOnlyAndValidateOnly_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-DetectOnly", "-ValidateOnly"]));
    }

    [Fact]
    public void MaxParallelism_DefaultsToNull()
    {
        Options options = Program.ParseArguments(["-BasePath", "."]);

        Assert.Null(options.MaxParallelism);
    }

    [Fact]
    public void MaxParallelism_ParsesPositiveInteger()
    {
        Options options = Program.ParseArguments(["-BasePath", ".", "-MaxParallelism", "7"]);

        Assert.Equal(7, options.MaxParallelism);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void MaxParallelism_RejectsNonPositiveOrNonNumeric(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-MaxParallelism", value]));
    }

    [Fact]
    public void MaxParallelism_MissingValue_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-MaxParallelism"]));
    }

    [Theory]
    [InlineData("-BasePath", "-Verbose")]
    [InlineData("-Include", "-Exclude")]
    [InlineData("-Target", "-WhatIf")]
    [InlineData("-Report", "-MaxParallelism")]
    public void ValueLooksLikeAnotherKnownFlag_IsTreatedAsMissingValue(string flag, string nextFlag)
    {
        // e.g. "-BasePath -Verbose" must not silently set BasePath = "-Verbose" and
        // swallow -Verbose as that value instead of parsing it as its own flag.
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", flag, nextFlag]));
    }

    [Fact]
    public void MaxParallelism_ValueThatStartsWithDashButIsNotAKnownFlag_IsConsumedAsTheValue()
    {
        // A negative-number-shaped value must not be mistaken for a missing flag value -
        // it's correctly consumed, then rejected for being non-positive.
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Program.ParseArguments(["-BasePath", ".", "-MaxParallelism", "-4"]));

        Assert.Contains("positive integer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
