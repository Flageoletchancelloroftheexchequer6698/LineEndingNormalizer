using System.Buffers;
using System.Text;

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

        ByteScanResult scan =
            ScanFileBytes(
                source,
                target);

        if (scan.Encoding == null)
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
            scan.Encoding,
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

        Encoding? encoding =
            TextEncoding.DetectFromStream(source);

        if (encoding == null)
        {
            return null;
        }

        //
        // Legacy encodings are scanned directly as bytes; Unicode encodings
        // are strictly decoded.
        //
        LineEndingKind kind =
            TextEncoding.IsUnicodeEncoding(encoding)
                ? DetectLineEndingKind(source, encoding)
                : DetectLineEndingKindBytes(source);

        return new DetectResult(
            encoding,
            encoding.GetPreamble().Length > 0,
            kind);
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


    #region Byte Scanner

    /// <summary>
    /// Result of the initial encoding and line-ending scan.
    /// </summary>
    private sealed record ByteScanResult(
        Encoding? Encoding,
        bool RequiresConversion);


    /// <summary>
    /// Detects the encoding and determines whether line endings require
    /// conversion using a single streaming scan.
    /// </summary>
    private static ByteScanResult ScanFileBytes(
        FileStream stream,
        LineEnding target)
    {
        var encoding =
            TextEncoding.DetectFromStream(stream);

        if (encoding == null)
        {
            return new ByteScanResult(
                Encoding: null,
                RequiresConversion: false);
        }

        //
        // Legacy encodings are scanned as raw bytes; Unicode encodings
        // are strictly decoded.
        //
        bool requiresConversion =
            TextEncoding.IsUnicodeEncoding(encoding)
                ? ScanLineEndings(stream, encoding, target)
                : ScanLineEndingsBytes(stream, target);

        return new ByteScanResult(
            encoding,
            requiresConversion);
    }

    #endregion


    #region Line Ending Detection

    /// <summary>
    /// Determines whether a Unicode file contains line endings different
    /// from <paramref name="target"/>. Uses strict decoding and pooled buffers.
    /// </summary>
    private static bool ScanLineEndings(
        Stream stream,
        Encoding encoding,
        LineEnding target)
    {
        var decoder =
            encoding.GetDecoder();

        decoder.Fallback =
            DecoderFallback.ExceptionFallback;

        byte[] bytes =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        char[] chars =
            ArrayPool<char>.Shared.Rent(
                encoding.GetMaxCharCount(BufferSize));

        try
        {
            bool pendingCr =
                false;

            int read;

            while ((read =
                       stream.Read(
                           bytes,
                           0,
                           BufferSize)) > 0)
            {
                int count =
                    decoder.GetChars(
                        bytes,
                        0,
                        read,
                        chars,
                        0);

                for (int i = 0;
                     i < count;
                     i++)
                {
                    char c =
                        chars[i];

                    if (pendingCr)
                    {
                        if (c == '\n')
                        {
                            if (target != LineEnding.Crlf)
                            {
                                return true;
                            }

                            pendingCr = false;
                            continue;
                        }

                        if (target != LineEnding.Cr)
                        {
                            return true;
                        }

                        pendingCr = false;
                    }

                    if (c == '\r')
                    {
                        pendingCr = true;
                    }
                    else if (c == '\n')
                    {
                        if (target != LineEnding.Lf)
                        {
                            return true;
                        }
                    }
                    else if (c == '\u0085' ||
                             c == '\u2028' ||
                             c == '\u2029')
                    {
                        return true;
                    }
                }
            }

            decoder.GetChars(
                [],
                0,
                0,
                chars,
                0,
                true);

            //
            // Handle a final standalone CR.
            //
            if (pendingCr &&
                target != LineEnding.Cr)
            {
                return true;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }
    }


    /// <summary>
    /// Determines whether a legacy-encoded file contains line endings
    /// different from <paramref name="target"/> by scanning raw CR/LF bytes.
    /// This avoids relying on code-page decoder fallback behavior.
    /// </summary>
    private static bool ScanLineEndingsBytes(
        Stream stream,
        LineEnding target)
    {
        byte[] bytes =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            bool pendingCr =
                false;

            int read;

            while ((read =
                       stream.Read(
                           bytes,
                           0,
                           BufferSize)) > 0)
            {
                for (int i = 0;
                     i < read;
                     i++)
                {
                    byte b =
                        bytes[i];

                    if (pendingCr)
                    {
                        if (b == 0x0A)
                        {
                            if (target != LineEnding.Crlf)
                            {
                                return true;
                            }

                            pendingCr = false;
                            continue;
                        }

                        if (target != LineEnding.Cr)
                        {
                            return true;
                        }

                        pendingCr = false;
                    }

                    if (b == 0x0D)
                    {
                        pendingCr = true;
                    }
                    else if (b == 0x0A &&
                             target != LineEnding.Lf)
                    {
                        return true;
                    }
                }
            }

            //
            // Handle a final standalone CR.
            //
            if (pendingCr &&
                target != LineEnding.Cr)
            {
                return true;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }


    /// <summary>
    /// Classifies line endings in a Unicode file as CRLF, LF, CR, MIXED,
    /// or NONE. Uses strict decoding; NEL, LS, and PS are not classified.
    /// </summary>
    private static LineEndingKind DetectLineEndingKind(
        Stream stream,
        Encoding encoding)
    {
        var decoder =
            encoding.GetDecoder();

        decoder.Fallback =
            DecoderFallback.ExceptionFallback;

        byte[] bytes =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        char[] chars =
            ArrayPool<char>.Shared.Rent(
                encoding.GetMaxCharCount(BufferSize));

        try
        {
            bool sawCrLf = false;
            bool sawLf = false;
            bool sawCr = false;
            bool pendingCr = false;

            int read;

            while ((read =
                       stream.Read(
                           bytes,
                           0,
                           BufferSize)) > 0)
            {
                int count =
                    decoder.GetChars(
                        bytes,
                        0,
                        read,
                        chars,
                        0);

                for (int i = 0;
                     i < count;
                     i++)
                {
                    char c =
                        chars[i];

                    if (pendingCr)
                    {
                        pendingCr = false;

                        if (c == '\n')
                        {
                            sawCrLf = true;
                            continue;
                        }

                        sawCr = true;
                    }

                    if (c == '\r')
                    {
                        pendingCr = true;
                    }
                    else if (c == '\n')
                    {
                        sawLf = true;
                    }
                }
            }

            decoder.GetChars(
                [],
                0,
                0,
                chars,
                0,
                true);

            if (pendingCr)
            {
                sawCr = true;
            }

            int distinctKinds =
                (sawCrLf ? 1 : 0) +
                (sawLf ? 1 : 0) +
                (sawCr ? 1 : 0);

            if (distinctKinds == 0)
            {
                return LineEndingKind.None;
            }

            if (distinctKinds > 1)
            {
                return LineEndingKind.Mixed;
            }

            if (sawCrLf)
            {
                return LineEndingKind.Crlf;
            }

            return sawLf
                ? LineEndingKind.Lf
                : LineEndingKind.Cr;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }
    }


    /// <summary>
    /// Classifies line endings in a legacy-encoded file as CRLF, LF, CR,
    /// MIXED, or NONE using raw-byte scanning.
    /// </summary>
    private static LineEndingKind DetectLineEndingKindBytes(
        Stream stream)
    {
        byte[] bytes =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            bool sawCrLf = false;
            bool sawLf = false;
            bool sawCr = false;
            bool pendingCr = false;

            int read;

            while ((read =
                       stream.Read(
                           bytes,
                           0,
                           BufferSize)) > 0)
            {
                for (int i = 0;
                     i < read;
                     i++)
                {
                    byte b =
                        bytes[i];

                    if (pendingCr)
                    {
                        pendingCr = false;

                        if (b == 0x0A)
                        {
                            sawCrLf = true;
                            continue;
                        }

                        sawCr = true;
                    }

                    if (b == 0x0D)
                    {
                        pendingCr = true;
                    }
                    else if (b == 0x0A)
                    {
                        sawLf = true;
                    }
                }
            }

            if (pendingCr)
            {
                sawCr = true;
            }

            int distinctKinds =
                (sawCrLf ? 1 : 0) +
                (sawLf ? 1 : 0) +
                (sawCr ? 1 : 0);

            if (distinctKinds == 0)
            {
                return LineEndingKind.None;
            }

            if (distinctKinds > 1)
            {
                return LineEndingKind.Mixed;
            }

            if (sawCrLf)
            {
                return LineEndingKind.Crlf;
            }

            return sawLf
                ? LineEndingKind.Lf
                : LineEndingKind.Cr;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
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
