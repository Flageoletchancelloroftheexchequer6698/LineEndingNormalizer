namespace LineEndingNormalizer;

/// <summary>
/// Provides methods for detecting text encodings (via <see cref="TextEncoding"/>,
/// which covers both Unicode and, as a fallback, legacy single-byte/
/// multi-byte encodings) and normalizing line endings while preserving
/// the original encoding.
///
/// Features:
/// - Unicode (UTF-8/16/32) and legacy encoding support
/// - BOM preservation
/// - Streaming conversion
/// - Constant memory usage
/// - Atomic replacement
/// - File metadata preservation
/// </summary>
internal static class NewLineNormalizer
{
    private const int BufferSize = 65536;

    #region Public API

    /// <summary>
    /// Normalizes a file's line endings to <paramref name="target"/>.
    /// Preserves the source encoding and BOM. Rewrites the file only when
    /// conversion is required.
    /// </summary>
    public static NormalizeResult NormalizeFile(
        string path,
        LineEnding target,
        bool whatIf,
        bool backup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File not found.", path);
        }

        //
        // Reuse one open stream for detection, scanning, and conversion.
        // This avoids redundant opens and narrows the window for external
        // changes between processing stages.
        //
        using FileStream source =
            OpenSourceStream(path);

        ScanEngine.ScanResult? scan =
            ScanEngine.Scan(source, target, cancellationToken);

        if (scan == null)
        {
            return NormalizeResult.EncodingNotDetected;
        }

        if (!scan.RequiresConversion)
        {
            return NormalizeResult.Unchanged;
        }

        if (whatIf)
        {
            return NormalizeResult.Converted;
        }

        FileMetadata metadata =
            CaptureMetadata(path);

        //
        // Read-only destination handling and revalidation immediately
        // before replacement are owned entirely by ConvertFile, so this
        // caller (and any other caller of ConvertFile) gets the same
        // protection without needing to duplicate it.
        //
        LosslessFileWriter.ConvertFile(
            source,
            path,
            scan.Detection.Encoding,
            target,
            metadata,
            backup,
            cancellationToken);

        return NormalizeResult.Converted;
    }


    /// <summary>
    /// Performs a read-only scan used by <c>-DetectOnly</c> to detect
    /// encoding, BOM state, and line-ending style.
    /// </summary>
    /// <returns>
    /// The detection result, or <see langword="null"/> if the encoding
    /// could not be determined.
    /// </returns>
    /// <exception cref="DecoderFallbackException">
    /// The detected encoding fails strict decoding of the complete file.
    /// </exception>
    public static DetectResult? DetectFile(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(path);

        using FileStream source =
            OpenSourceStream(path);

        // No target: DetectOnly only needs classification, not a conversion decision.
        ScanEngine.ScanResult? scan =
            ScanEngine.Scan(source, target: null, cancellationToken);

        return scan?.Detection;
    }

    #endregion


    #region Source Opening

    /// <summary>
    /// Opens a source for detection, scanning, and optional conversion.
    /// Allows concurrent readers but prevents other processes from writing
    /// or deleting the file while the handle remains open.
    /// </summary>
    private static FileStream OpenSourceStream(
        string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
    }

    #endregion


    #region Metadata Capture

    /// <summary>
    /// Captures the original file metadata for preservation during replacement.
    /// </summary>
    private static FileMetadata CaptureMetadata(
        string path)
    {
        var info =
            new FileInfo(path);

        return new FileMetadata(
            info.Attributes,
            info.Length,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            info.LastAccessTimeUtc);
    }

    #endregion
}
