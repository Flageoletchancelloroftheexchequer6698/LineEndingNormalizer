namespace LineEndingNormalizer;

/// <summary>
/// A filesystem metadata snapshot captured before conversion, re-applied
/// to the temporary file before it replaces the original, and re-checked
/// against the live file immediately before replacement to detect a
/// concurrent external modification (see
/// <see cref="LosslessFileWriter"/>'s destination-revalidation step).
///
/// Immutable: a snapshot represents one point in time and must not be
/// mutated after capture. Timestamps are UTC so the revalidation
/// comparison is not affected by DST transitions between capture and
/// replacement.
/// </summary>
internal sealed record FileMetadata(
    FileAttributes Attributes,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    DateTime LastAccessTimeUtc);
