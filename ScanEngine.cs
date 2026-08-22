using System.Buffers;
using System.Text;

namespace LineEndingNormalizer;

/// <summary>
/// Read-only file scanning shared by DetectOnly, ValidateOnly, and Convert, so
/// all three agree on encoding, line-ending classification, and whether the
/// requested target would change the file. Never opens, closes, or takes
/// ownership of the stream it's given.
/// </summary>
internal static class ScanEngine
{
    private const int BufferSize = 65536;

    /// <summary>
    /// Detects the encoding and scans the complete stream once to determine
    /// line-ending classification and, when <paramref name="target"/> is
    /// given, whether normalizing to it would change the file.
    /// </summary>
    internal static (DetectResult? Detected, bool RequiresConversion) Scan(
        Stream stream,
        LineEnding? target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        cancellationToken.ThrowIfCancellationRequested();

        Encoding? encoding = TextEncoding.DetectFromStream(stream);

        if (encoding == null)
        {
            return (null, false);
        }

        (LineEndingKind kind, bool requiresConversion) =
            TextEncoding.IsUnicodeEncoding(encoding)
                ? ScanUnicode(stream, encoding, target, cancellationToken)
                : ScanBytes(stream, target, cancellationToken);

        var detected = new DetectResult(
            encoding,
            encoding.GetPreamble().Length > 0,
            kind);

        return (detected, requiresConversion);
    }

    /// <summary>
    /// Strictly decodes the stream once, classifying line endings and
    /// checking them against <paramref name="target"/> in the same pass.
    /// </summary>
    private static (LineEndingKind Kind, bool RequiresConversion) ScanUnicode(
        Stream stream,
        Encoding encoding,
        LineEnding? target,
        CancellationToken cancellationToken)
    {
        var decoder = encoding.GetDecoder();

        // Reject malformed Unicode instead of silently replacing it.
        decoder.Fallback = DecoderFallback.ExceptionFallback;

        byte[] bytes = ArrayPool<byte>.Shared.Rent(BufferSize);

        char[] chars = ArrayPool<char>.Shared.Rent(
            encoding.GetMaxCharCount(BufferSize));

        try
        {
            bool sawCrLf = false;
            bool sawLf = false;
            bool sawCr = false;
            bool pendingCr = false;

            // NEL/LS/PS force conversion but are excluded from classification,
            // matching the existing DetectOnly contract.
            bool sawUnicodeLineSeparator = false;

            int read;

            while ((read = stream.Read(bytes, 0, BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int count = decoder.GetChars(bytes, 0, read, chars, 0);

                for (int i = 0; i < count; i++)
                {
                    char c = chars[i];

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
                    else if (c == '\u0085' || c == '\u2028' || c == '\u2029')
                    {
                        sawUnicodeLineSeparator = true;
                    }
                }
            }

            // Final flush rejects an incomplete trailing sequence.
            decoder.GetChars([], 0, 0, chars, 0, true);

            if (pendingCr)
            {
                sawCr = true;
            }

            LineEndingKind kind = Classify(sawCrLf, sawLf, sawCr);

            bool requiresConversion =
                target is LineEnding t &&
                (sawUnicodeLineSeparator ||
                 (sawCrLf && t != LineEnding.Crlf) ||
                 (sawLf && t != LineEnding.Lf) ||
                 (sawCr && t != LineEnding.Cr));

            return (kind, requiresConversion);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>
    /// Scans raw CR/LF bytes once, classifying line endings and checking
    /// them against <paramref name="target"/> in the same pass. Legacy files
    /// are scanned as bytes, not decoded, to avoid decode/re-encode changes.
    /// </summary>
    private static (LineEndingKind Kind, bool RequiresConversion) ScanBytes(
        Stream stream,
        LineEnding? target,
        CancellationToken cancellationToken)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            bool sawCrLf = false;
            bool sawLf = false;
            bool sawCr = false;
            bool pendingCr = false;

            int read;

            while ((read = stream.Read(bytes, 0, BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < read; i++)
                {
                    byte b = bytes[i];

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

            LineEndingKind kind = Classify(sawCrLf, sawLf, sawCr);

            bool requiresConversion =
                target is LineEnding t &&
                ((sawCrLf && t != LineEnding.Crlf) ||
                 (sawLf && t != LineEnding.Lf) ||
                 (sawCr && t != LineEnding.Cr));

            return (kind, requiresConversion);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Classifies line endings from the accumulated per-kind flags. Shared by
    /// both scan paths; previously duplicated verbatim in each.
    /// </summary>
    private static LineEndingKind Classify(bool sawCrLf, bool sawLf, bool sawCr)
    {
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

        return sawLf ? LineEndingKind.Lf : LineEndingKind.Cr;
    }
}
