using System.Buffers;
using System.ComponentModel;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace LineEndingNormalizer;

/// <summary>
/// Rewrites files while preserving encoding, BOM state, and metadata.
/// Writes and verifies a temporary file before replacing the destination.
/// Unicode encodings use strict decode/normalize/encode; approved legacy
/// encodings use raw byte-level normalization.
/// </summary>
internal static class LosslessFileWriter
{
    private const int BufferSize = 65536;

    /// <summary>
    /// Unicode characters recognized as line breaks during normalization.
    /// </summary>
    /// <remarks>
    /// CR, LF, NEL, LS, and PS.
    /// </remarks>
    private static readonly SearchValues<char> LineBreakChars =
        SearchValues.Create(['\r', '\n', '\u0085', '\u2028', '\u2029']);

    #region Public API

    /// <summary>
    /// Rewrites <paramref name="path"/> with normalized line endings while
    /// preserving its encoding, BOM state, and metadata.
    /// Verifies the temporary file and revalidates the destination before
    /// replacement.
    /// </summary>
    /// <param name="source">Open, seekable source stream. Ownership transfers here.</param>
    /// <param name="path">File to rewrite.</param>
    /// <param name="encoding">Detected source encoding.</param>
    /// <param name="target">Target line-ending style.</param>
    /// <param name="metadata">Original file metadata used for preservation and revalidation.</param>
    /// <param name="createBackup">Creates or overwrites a <c>.bak</c> backup when <see langword="true"/>.</param>
    /// <param name="cancellationToken">Token checked between conversion and transactional steps.</param>
    /// <exception cref="Exception">Thrown if conversion, verification, validation, or replacement fails.</exception>
    public static void ConvertFile(
        FileStream source,
        string path,
        Encoding encoding,
        LineEnding target,
        FileMetadata metadata,
        bool createBackup,
        CancellationToken cancellationToken = default)
    {
        // Validate inside the try so the transferred stream is always disposed.
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(encoding);
            ArgumentNullException.ThrowIfNull(metadata);

            if (!source.CanSeek)
            {
                throw new ArgumentException(
                    "The source stream must be seekable.",
                    nameof(source));
            }

            RejectReparsePoints(path, metadata.Attributes);

            string directory =
                Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "Cannot determine the directory for " + path);

            // Keep the temporary name short to reduce path-length risk.
            string tempPath =
                Path.Combine(
                    directory,
                    Path.GetFileName(path) +
                    "." +
                    Guid.NewGuid().ToString("N")[..12] +
                    ".len.tmp");

            byte[] preamble =
                encoding.GetPreamble();

            // Validate the detected BOM before skipping it.
            ValidatePreamble(source, preamble);

            // Unicode uses strict decode/encode.
            // Approved legacy encodings use raw byte-level normalization.
            bool isUnicode =
                TextEncoding.IsUnicodeEncoding(encoding);

            if (!isUnicode)
            {
                EnsureSafeLegacyEncoding(encoding);
            }

            try
            {
                // Preserve the original BOM and exclude it from conversion.
                source.Position = preamble.Length;

                byte[] expectedHash =
                    isUnicode
                        ? WriteConvertedFileUnicode(
                            source,
                            encoding,
                            target,
                            tempPath,
                            preamble,
                            cancellationToken)
                        : WriteConvertedFileBytes(
                            source,
                            target,
                            tempPath,
                            cancellationToken);

                // Release the source share-lock before revalidation and replacement.
                source.Dispose();

                cancellationToken.ThrowIfCancellationRequested();

                if (isUnicode)
                {
                    VerifyConvertedFileUnicode(
                        tempPath,
                        encoding,
                        preamble,
                        expectedHash,
                        cancellationToken);
                }
                else
                {
                    VerifyConvertedFileBytes(
                        tempPath,
                        expectedHash,
                        cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Revalidate after releasing the source share-lock.
                RevalidateDestination(path, metadata);

                // Apply metadata before replacement.
                ApplyMetadata(tempPath, metadata);

                cancellationToken.ThrowIfCancellationRequested();

                // Clear ReadOnly temporarily if required for replacement.
                bool clearedReadOnly =
                    PrepareDestinationForReplacement(
                        path,
                        metadata.Attributes);

                try
                {
                    if (createBackup)
                    {
                        CreateBackup(path, directory);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    AtomicReplace(tempPath, path);
                }
                catch
                {
                    if (clearedReadOnly)
                    {
                        RestoreDestinationAttributes(
                            path,
                            metadata.Attributes);
                    }

                    throw;
                }
            }
            finally
            {
                // ReplaceFile consumes the temporary file on success.
                try
                {
                    File.Delete(tempPath);
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    #endregion


    #region Reparse Points

    /// <summary>
    /// Rejects symbolic links, junctions, and other reparse points.
    /// </summary>
    private static void RejectReparsePoints(
        string path,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException(
                "Refusing to convert a symbolic link, junction, or other reparse point: " +
                path);
        }
    }

    #endregion


    #region BOM Validation

    /// <summary>
    /// Verifies that the source begins with the expected BOM.
    /// </summary>
    private static void ValidatePreamble(
        FileStream source,
        byte[] preamble)
    {
        if (preamble.Length == 0)
        {
            return;
        }

        if (source.Length < preamble.Length)
        {
            throw new InvalidDataException(
                "The source file is shorter than the byte-order mark implied by its detected encoding.");
        }

        byte[] actual =
            new byte[preamble.Length];

        source.Position = 0;

        int read =
            source.ReadAtLeast(
                actual,
                preamble.Length,
                throwOnEndOfStream: false);

        if (read != preamble.Length ||
            !actual.AsSpan().SequenceEqual(preamble))
        {
            throw new InvalidDataException(
                "The source file's leading bytes do not match the byte-order mark implied by its detected encoding.");
        }
    }

    #endregion


    #region Legacy Encoding Safety

    /// <summary>
    /// Ensures the legacy encoding is on the approved raw-byte allowlist.
    /// </summary>
    /// <remarks>
    /// This is a final safety check at the mutation boundary.
    /// </remarks>
    private static void EnsureSafeLegacyEncoding(
        Encoding encoding)
    {
        if (!TextEncoding.IsSafeLegacyEncoding(encoding))
        {
            throw new NotSupportedException(
                "Refusing to convert using a legacy encoding not verified safe for raw byte-level line-ending normalization: " +
                encoding.WebName);
        }
    }

    #endregion


    #region Destination Revalidation

    /// <summary>
    /// Checks that the destination still exists, is not a reparse point,
    /// and matches its original length and last-write time.
    /// </summary>
    /// <remarks>
    /// This is a point-in-time race check, not a complete TOCTOU guarantee.
    /// A change can still occur between this check and replacement.
    /// </remarks>
    private static void RevalidateDestination(
        string path,
        FileMetadata metadata)
    {
        var info =
            new FileInfo(path);

        if (!info.Exists)
        {
            throw new IOException(
                "The source file no longer exists; it may have been deleted or moved during conversion: " +
                path);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException(
                "Refusing to replace a file that became a symbolic link, junction, or other reparse point during conversion: " +
                path);
        }

        if (info.Length != metadata.Length ||
            info.LastWriteTimeUtc != metadata.LastWriteTimeUtc)
        {
            throw new IOException(
                "The source file changed during conversion; refusing to replace it: " +
                path);
        }
    }

    #endregion


    #region Read-Only Destination Handling

    /// <summary>
    /// Clears ReadOnly when necessary for replacement and returns whether it
    /// was changed.
    /// </summary>
    private static bool PrepareDestinationForReplacement(
        string path,
        FileAttributes originalAttributes)
    {
        bool wasReadOnly =
            (originalAttributes & FileAttributes.ReadOnly) != 0;

        if (wasReadOnly)
        {
            File.SetAttributes(
                path,
                originalAttributes & ~FileAttributes.ReadOnly);
        }

        return wasReadOnly;
    }


    /// <summary>
    /// Restores the original destination attributes after a failed replacement.
    /// </summary>
    private static void RestoreDestinationAttributes(
        string path,
        FileAttributes originalAttributes)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, originalAttributes);
        }
    }

    #endregion


    #region Metadata Application

    /// <summary>
    /// Applies the original file metadata to the temporary file.
    /// </summary>
    private static void ApplyMetadata(
        string tempPath,
        FileMetadata metadata)
    {
        File.SetCreationTimeUtc(
            tempPath,
            metadata.CreationTimeUtc);

        File.SetLastWriteTimeUtc(
            tempPath,
            metadata.LastWriteTimeUtc);

        File.SetLastAccessTimeUtc(
            tempPath,
            metadata.LastAccessTimeUtc);

        File.SetAttributes(
            tempPath,
            metadata.Attributes);
    }

    #endregion


    #region Backup

    /// <summary>
    /// Creates or overwrites <c>&lt;path&gt;.bak</c> from the current destination.
    /// The backup is written to a temporary file and atomically installed.
    /// </summary>
    /// <remarks>
    /// Backup creation and main replacement are separate filesystem operations.
    /// The main replacement is attempted only after backup creation succeeds.
    /// </remarks>
    private static void CreateBackup(
        string path,
        string directory)
    {
        string backupTempPath =
            Path.Combine(
                directory,
                Path.GetFileName(path) +
                "." +
                Guid.NewGuid().ToString("N")[..12] +
                ".bak.tmp");

        try
        {
            using (FileStream backupSource =
                       new(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           BufferSize,
                           FileOptions.SequentialScan))
            using (FileStream backupOutput =
                       new(
                           backupTempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           BufferSize,
                           FileOptions.SequentialScan))
            {
                backupSource.CopyTo(backupOutput, BufferSize);
                backupOutput.Flush(true);
            }

            AtomicReplace(
                backupTempPath,
                path + ".bak");
        }
        finally
        {
            try
            {
                File.Delete(backupTempPath);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    #endregion


    #region Unicode Strategy (decode / normalize / encode)

    /// <summary>
    /// Strictly decodes, normalizes, and re-encodes the source into a temporary
    /// file. Returns a hash of the normalized Unicode content.
    /// </summary>
    private static byte[] WriteConvertedFileUnicode(
        FileStream source,
        Encoding encoding,
        LineEnding target,
        string tempPath,
        byte[] preamble,
        CancellationToken cancellationToken)
    {
        int maxReplacementLength =
            target == LineEnding.Crlf ? 2 : 1;

        int decodedCapacity =
            encoding.GetMaxCharCount(BufferSize);

        int normalizedCapacity =
            decodedCapacity * maxReplacementLength + maxReplacementLength;

        int outputCapacity =
            encoding.GetMaxByteCount(normalizedCapacity);

        byte[] inputBuffer =
            ArrayPool<byte>.Shared.Rent(BufferSize);
        char[] decodedBuffer =
            ArrayPool<char>.Shared.Rent(decodedCapacity);
        char[] normalizedBuffer =
            ArrayPool<char>.Shared.Rent(normalizedCapacity);
        byte[] outputBuffer =
            ArrayPool<byte>.Shared.Rent(outputCapacity);

        try
        {
            using FileStream output =
                new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan);

            if (preamble.Length > 0)
            {
                output.Write(
                    preamble,
                    0,
                    preamble.Length);
            }

            Decoder decoder =
                encoding.GetDecoder();

            decoder.Fallback =
                DecoderFallback.ExceptionFallback;

            Encoder encoder =
                encoding.GetEncoder();

            encoder.Fallback =
                EncoderFallback.ExceptionFallback;

            // Hash normalized content for verification.
            var hasher =
                new XxHash3();

            bool pendingCr = false;

            int read;

            while ((read =
                       source.Read(
                           inputBuffer,
                           0,
                           BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int decodedCount =
                    decoder.GetChars(
                        inputBuffer,
                        0,
                        read,
                        decodedBuffer,
                        0,
                        flush: false);

                int normalizedCount =
                    NormalizeChars(
                        decodedBuffer.AsSpan(0, decodedCount),
                        normalizedBuffer,
                        target,
                        ref pendingCr,
                        isFinal: false);

                hasher.Append(
                    MemoryMarshal.AsBytes(
                        normalizedBuffer.AsSpan(0, normalizedCount)));

                int byteCount =
                    encoder.GetBytes(
                        normalizedBuffer,
                        0,
                        normalizedCount,
                        outputBuffer,
                        0,
                        flush: false);

                output.Write(
                    outputBuffer,
                    0,
                    byteCount);
            }

            // Final flush rejects an incomplete source sequence.
            int finalDecodedCount =
                decoder.GetChars(
                    [],
                    0,
                    0,
                    decodedBuffer,
                    0,
                    flush: true);

            int finalNormalizedCount =
                NormalizeChars(
                    decodedBuffer.AsSpan(0, finalDecodedCount),
                    normalizedBuffer,
                    target,
                    ref pendingCr,
                    isFinal: true);

            hasher.Append(
                MemoryMarshal.AsBytes(
                    normalizedBuffer.AsSpan(0, finalNormalizedCount)));

            // Final flush rejects unrepresentable characters.
            int finalByteCount =
                encoder.GetBytes(
                    normalizedBuffer,
                    0,
                    finalNormalizedCount,
                    outputBuffer,
                    0,
                    flush: true);

            output.Write(
                outputBuffer,
                0,
                finalByteCount);

            // Flush before verification.
            output.Flush(true);

            return hasher.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<char>.Shared.Return(decodedBuffer);
            ArrayPool<char>.Shared.Return(normalizedBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }


    /// <summary>
    /// Normalizes Unicode line endings to <paramref name="target"/>.
    /// Carries a trailing CR across buffer boundaries.
    /// </summary>
    private static int NormalizeChars(
        ReadOnlySpan<char> input,
        Span<char> output,
        LineEnding target,
        ref bool pendingCr,
        bool isFinal)
    {
        ReadOnlySpan<char> replacement = target switch
        {
            LineEnding.Crlf => "\r\n",
            LineEnding.Lf => "\n",
            LineEnding.Cr => "\r",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        int outPos = 0;
        int i = 0;
        int n = input.Length;

        while (i < n)
        {
            if (pendingCr)
            {
                // Resolve a CR carried from the previous chunk.
                replacement.CopyTo(output[outPos..]);
                outPos += replacement.Length;
                pendingCr = false;

                char c0 = input[i];

                if (c0 == '\n')
                {
                    i++;
                    continue;
                }

                if (c0 == '\r')
                {
                    pendingCr = true;
                }
                else if (c0 == '\u0085' ||
                         c0 == '\u2028' ||
                         c0 == '\u2029')
                {
                    replacement.CopyTo(output[outPos..]);
                    outPos += replacement.Length;
                }
                else
                {
                    output[outPos++] = c0;
                }

                i++;
                continue;
            }

            ReadOnlySpan<char> remaining =
                input[i..];

            int next =
                remaining.IndexOfAny(LineBreakChars);

            if (next < 0)
            {
                remaining.CopyTo(output[outPos..]);
                outPos += remaining.Length;
                break;
            }

            if (next > 0)
            {
                ReadOnlySpan<char> run =
                    remaining[..next];

                run.CopyTo(output[outPos..]);
                outPos += run.Length;
            }

            char c =
                remaining[next];

            i += next + 1;

            if (c == '\r')
            {
                pendingCr = true;
            }
            else
            {
                replacement.CopyTo(output[outPos..]);
                outPos += replacement.Length;
            }
        }

        if (isFinal && pendingCr)
        {
            replacement.CopyTo(output[outPos..]);
            outPos += replacement.Length;
            pendingCr = false;
        }

        return outPos;
    }


    /// <summary>
    /// Verifies the temporary file's BOM and decoded-content hash.
    /// Uses strict decoding to detect invalid sequences.
    /// </summary>
    /// <remarks>
    /// The hash covers normalized Unicode characters rather than encoded bytes,
    /// matching the Unicode conversion model.
    /// </remarks>
    private static void VerifyConvertedFileUnicode(
        string tempPath,
        Encoding encoding,
        byte[] preamble,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        using FileStream temp =
            new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);

        byte[] actualPreamble =
            new byte[preamble.Length];

        int preambleRead =
            preamble.Length == 0
                ? 0
                : temp.ReadAtLeast(
                    actualPreamble,
                    preamble.Length,
                    throwOnEndOfStream: false);

        if (preambleRead != preamble.Length ||
            !actualPreamble
                .AsSpan(0, preambleRead)
                .SequenceEqual(preamble))
        {
            throw new InvalidDataException(
                "Verification failed: the target BOM state does not match the expected encoding.");
        }

        int decodedCapacity =
            encoding.GetMaxCharCount(BufferSize);

        byte[] inputBuffer =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        char[] decodedBuffer =
            ArrayPool<char>.Shared.Rent(decodedCapacity);

        try
        {
            Decoder decoder =
                encoding.GetDecoder();

            decoder.Fallback =
                DecoderFallback.ExceptionFallback;

            var hasher =
                new XxHash3();

            int read;

            while ((read =
                       temp.Read(
                           inputBuffer,
                           0,
                           BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int count =
                    decoder.GetChars(
                        inputBuffer,
                        0,
                        read,
                        decodedBuffer,
                        0,
                        flush: false);

                hasher.Append(
                    MemoryMarshal.AsBytes(
                        decodedBuffer.AsSpan(0, count)));
            }

            // Final flush rejects an incomplete sequence.
            int finalCount =
                decoder.GetChars(
                    [],
                    0,
                    0,
                    decodedBuffer,
                    0,
                    flush: true);

            hasher.Append(
                MemoryMarshal.AsBytes(
                    decodedBuffer.AsSpan(0, finalCount)));

            byte[] actualHash =
                hasher.GetHashAndReset();

            if (!actualHash
                .AsSpan()
                .SequenceEqual(expectedHash))
            {
                throw new InvalidDataException(
                    "Verification failed: the target content does not match the source content.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<char>.Shared.Return(decodedBuffer);
        }
    }

    #endregion


    #region Legacy Strategy (raw byte scan)

    /// <summary>
    /// Normalizes CR/LF bytes directly without decoding or re-encoding.
    /// Returns a hash of the output bytes.
    /// </summary>
    private static byte[] WriteConvertedFileBytes(
        FileStream source,
        LineEnding target,
        string tempPath,
        CancellationToken cancellationToken)
    {
        // Allow CRLF expansion and a carried CR.
        int outputCapacity =
            BufferSize * 2 + 2;

        byte[] inputBuffer =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        byte[] outputBuffer =
            ArrayPool<byte>.Shared.Rent(outputCapacity);

        try
        {
            using FileStream output =
                new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan);

            var hasher =
                new XxHash3();

            bool pendingCr = false;

            int read;

            while ((read =
                       source.Read(
                           inputBuffer,
                           0,
                           BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int normalizedCount =
                    NormalizeBytes(
                        inputBuffer.AsSpan(0, read),
                        outputBuffer,
                        target,
                        ref pendingCr,
                        isFinal: false);

                hasher.Append(
                    outputBuffer.AsSpan(0, normalizedCount));

                output.Write(
                    outputBuffer,
                    0,
                    normalizedCount);
            }

            // Resolve a trailing CR.
            int finalNormalizedCount =
                NormalizeBytes(
                    [],
                    outputBuffer,
                    target,
                    ref pendingCr,
                    isFinal: true);

            if (finalNormalizedCount > 0)
            {
                hasher.Append(
                    outputBuffer.AsSpan(0, finalNormalizedCount));

                output.Write(
                    outputBuffer,
                    0,
                    finalNormalizedCount);
            }

            output.Flush(true);

            return hasher.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }


    /// <summary>
    /// Normalizes raw CR/LF bytes to <paramref name="target"/>.
    /// Carries a trailing CR across buffer boundaries.
    /// </summary>
    private static int NormalizeBytes(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        LineEnding target,
        ref bool pendingCr,
        bool isFinal)
    {
        ReadOnlySpan<byte> replacement = target switch
        {
            LineEnding.Crlf => [0x0D, 0x0A],
            LineEnding.Lf => [0x0A],
            LineEnding.Cr => [0x0D],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        int outPos = 0;
        int i = 0;
        int n = input.Length;

        while (i < n)
        {
            if (pendingCr)
            {
                // Resolve a CR carried from the previous chunk.
                replacement.CopyTo(output[outPos..]);
                outPos += replacement.Length;
                pendingCr = false;

                byte b0 = input[i];

                if (b0 == 0x0A)
                {
                    i++;
                    continue;
                }

                if (b0 == 0x0D)
                {
                    pendingCr = true;
                }
                else
                {
                    output[outPos++] = b0;
                }

                i++;
                continue;
            }

            ReadOnlySpan<byte> remaining =
                input[i..];

            int next =
                remaining.IndexOfAny(
                    (byte)0x0D,
                    (byte)0x0A);

            if (next < 0)
            {
                remaining.CopyTo(output[outPos..]);
                outPos += remaining.Length;
                break;
            }

            if (next > 0)
            {
                ReadOnlySpan<byte> run =
                    remaining[..next];

                run.CopyTo(output[outPos..]);
                outPos += run.Length;
            }

            byte b =
                remaining[next];

            i += next + 1;

            if (b == 0x0D)
            {
                pendingCr = true;
            }
            else
            {
                replacement.CopyTo(output[outPos..]);
                outPos += replacement.Length;
            }
        }

        if (isFinal && pendingCr)
        {
            replacement.CopyTo(output[outPos..]);
            outPos += replacement.Length;
            pendingCr = false;
        }

        return outPos;
    }


    /// <summary>
    /// Verifies the temporary file by hashing its raw output bytes.
    /// </summary>
    /// <remarks>
    /// Raw-byte hashing matches the legacy path because it performs no
    /// decoding or re-encoding.
    /// </remarks>
    private static void VerifyConvertedFileBytes(
        string tempPath,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        using FileStream temp =
            new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);

        byte[] inputBuffer =
            ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            var hasher =
                new XxHash3();

            int read;

            while ((read =
                       temp.Read(
                           inputBuffer,
                           0,
                           BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                hasher.Append(
                    inputBuffer.AsSpan(0, read));
            }

            byte[] actualHash =
                hasher.GetHashAndReset();

            if (!actualHash
                .AsSpan()
                .SequenceEqual(expectedHash))
            {
                throw new InvalidDataException(
                    "Verification failed: the target content does not match the source content.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
        }
    }

    #endregion


    #region Atomic Replacement

    private const int ReplaceFileIgnoreMergeErrors =
        0x00000002;

    /// <summary>
    /// Replaces the destination using Windows ReplaceFile when available
    /// and applicable; otherwise uses File.Move as a portable best-effort
    /// fallback.
    /// </summary>
    private static void AtomicReplace(
        string source,
        string destination)
    {
        // ReplaceFile requires an existing destination.
        if (IsWindows() && File.Exists(destination))
        {
            ReplaceFileWindows(
                source,
                destination);

            return;
        }

        // Portable fallback; atomicity depends on the OS and filesystem.
        File.Move(
            source,
            destination,
            overwrite: true);
    }


    /// <summary>
    /// Replaces <paramref name="destination"/> using Windows ReplaceFile.
    /// Falls back to File.Move only when the API is unavailable.
    /// </summary>
    private static void ReplaceFileWindows(
        string source,
        string destination)
    {
        bool succeeded;

        try
        {
            succeeded =
                ReplaceFileNative(
                    destination,
                    source,
                    null,
                    ReplaceFileIgnoreMergeErrors,
                    IntPtr.Zero,
                    IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            // Fall back only when the API itself is unavailable.
            File.Move(
                source,
                destination,
                overwrite: true);

            return;
        }
        catch (EntryPointNotFoundException)
        {
            // Fall back only when the API itself is unavailable.
            File.Move(
                source,
                destination,
                overwrite: true);

            return;
        }

        if (succeeded)
        {
            return;
        }

        int error =
            Marshal.GetLastWin32Error();

        throw new Win32Exception(
            error,
            $"ReplaceFile failed while replacing '{destination}' (Win32 error {error}).");
    }


    private static bool IsWindows()
    {
        return Environment.OSVersion.Platform ==
               PlatformID.Win32NT;
    }


    // EntryPoint must name the actual kernel32 export.
    [DllImport(
        "kernel32.dll",
        EntryPoint = "ReplaceFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool ReplaceFileNative(
        string replacedFileName,
        string replacementFileName,
        string? backupFileName,
        int replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    #endregion
}
