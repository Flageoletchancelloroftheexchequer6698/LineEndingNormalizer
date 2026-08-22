namespace LineEndingNormalizer;

/// <summary>
/// Outcome of a single-file normalization attempt.
/// </summary>
internal enum NormalizeResult
{
    /// <summary>
    /// The file already uses the requested line-ending style, so no
    /// rewrite was performed.
    /// </summary>
    Unchanged,

    /// <summary>
    /// The file was rewritten with the requested line-ending style,
    /// or would have been rewritten in preview mode.
    /// </summary>
    Converted,

    /// <summary>
    /// The file's encoding could not be determined, so no conversion
    /// was attempted. The file's line-ending style is therefore unknown.
    /// </summary>
    EncodingNotDetected
}
