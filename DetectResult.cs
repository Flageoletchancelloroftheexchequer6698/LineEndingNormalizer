using System.Text;

namespace LineEndingNormalizer;

/// <summary>
/// The outcome of a -DetectOnly scan of a single file: its encoding, BOM
/// state, and observed line-ending style. Immutable.
/// </summary>
internal sealed record DetectResult(
    Encoding Encoding,
    bool HasBom,
    LineEndingKind LineEndingKind);
