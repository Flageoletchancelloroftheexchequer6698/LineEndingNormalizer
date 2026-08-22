namespace LineEndingNormalizer;

/// <summary>
/// Processing mode for eligible files; <c>-DetectOnly</c> is a separate
/// read-only mode.
/// </summary>
internal enum ProcessingMode
{
    /// <summary>Rewrite files that need conversion.</summary>
    Normalize,

    /// <summary>Report files that would be converted without
    /// modifying them.
    /// </summary>
    WhatIf,

    /// <summary>Report files that are not already normalized
    /// without modifying them.
    /// </summary>
    ValidateOnly
}
