# LineEndingNormalizer

LineEndingNormalizer (LEN) is a Windows command-line tool that detects a
text file's encoding and line-ending style, and normalizes its line endings
to CRLF, LF, or CR while preserving the file's encoding, byte-order mark,
and metadata.

## Overview

LEN:

- detects Unicode encodings (UTF-8, UTF-16LE/BE, UTF-32LE/BE, with or without
  a byte-order mark) and a verified set of legacy single-byte and multi-byte
  encodings;
- normalizes CRLF, LF, and CR line endings to a chosen target style;
- also normalizes Unicode NEL (U+0085), LS (U+2028), and PS (U+2029) line
  separators to the target style in Unicode files, though they are not
  counted toward the CRLF/LF/CR/Mixed/None classification reported by
  `-DetectOnly` or `-Report` (see [Safety model](#safety-model));
- preserves the detected encoding and byte-order-mark state;
- preserves file attributes and timestamps;
- streams through files with fixed-size buffers, so processing time scales
  with file size but memory does not;
- writes to a temporary file, verifies it, revalidates the destination, and
  installs it with Windows' atomic `ReplaceFile` API when available, falling
  back to a plain move only when that API is unavailable;
- can back up the original file before overwriting it;
- responds to Ctrl+C and stops without leaving a half-written file behind;
- offers `-WhatIf`, `-ValidateOnly`, deterministic output, and dedicated exit
  codes for use as a CI gate.

## Highlights

- Unicode: UTF-8, UTF-16 (LE/BE), UTF-32 (LE/BE), with or without BOM.
- Legacy encodings: any single-byte code page where CR and LF keep their
  ASCII byte values, plus an explicitly verified set of multi-byte code pages
  (see [Supported charsets](#supported-charsets)).
- Line-ending normalization to CRLF, LF, or CR, including Unicode NEL/LS/PS.
- `-WhatIf` — report what would change without writing anything.
- `-ValidateOnly` — report files that don't already match the target, without
  writing anything.
- `-DetectOnly` — read-only encoding/line-ending detection, CSV to stdout.
- `-Backup` — copy the original to `<file>.bak` before overwriting it.
- `-Report` — write a CSV report alongside normal console output, in any
  mode.
- Bounded parallelism (`-MaxParallelism`), with a deterministic single-order
  mode (`-Deterministic`) for reproducible console/CI output.
- File attributes and timestamps preserved across conversion.
- Reparse-point roots and files are rejected rather than silently followed.
- Temp-file-then-atomic-replace conversion with content verification and a
  destination revalidation check immediately before install.
- `-FailOnChanges` and a dedicated exit code per failure category, for CI
  gating.

## Safety model

These are the guarantees the implementation actually provides — see the
referenced source files for the exact logic.

- Unicode files are decoded and re-encoded with a strict decoder/encoder
  (`DecoderFallback.ExceptionFallback` / `EncoderFallback.ExceptionFallback`):
  malformed input is rejected, never silently replaced or corrupted
  (`ScanEngine.cs`, `LosslessFileWriter.cs`).
- Legacy encodings are only normalized as raw bytes when verified safe: every
  single-byte code page is checked generically (CR/LF must decode to their
  ASCII byte values), and multi-byte code pages require explicit membership
  in a verified allowlist (`TextEncoding.IsSafeLegacyEncoding`).
- The source file is never rewritten in place. `NormalizeFile` writes the
  converted content to a new temporary file beside the destination
  (`LosslessFileWriter.ConvertFile`).
- The temporary file's content is verified (a hash of the normalized output)
  before installation, and its BOM state is separately verified.
- Immediately before installation, the destination is revalidated (existence,
  length, last-write time, and that it hasn't become a reparse point) so a
  file changed by something else during conversion is not silently
  overwritten. **This is a point-in-time race check, not a complete
  elimination of every possible TOCTOU window** — a change between that
  check and the replace itself is still possible.
- Original file attributes and timestamps are captured before conversion and
  applied to the temporary file before installation, so the final file's
  metadata is correct atomically along with its content.
- With `-Backup`, the original is copied to `<file>.bak` *before* the main
  file is replaced; if the backup fails, the main conversion is aborted and
  the original is left untouched. A previously read-only `.bak` is still
  replaced correctly.
- `-BasePath` itself is rejected if it is a symbolic link, junction, or other
  reparse point (exit code 6). Reparse-point subdirectories are skipped
  during traversal, and a file that is (or becomes) a reparse point is
  rejected at the point of conversion.
- `.bak` files and LEN's own abandoned temporary files (left behind only if
  the process is killed mid-conversion) are automatically excluded from
  scanning, including under a broad `-Include "*"`, so a later run never
  treats its own output as input.
- Installation prefers Windows' atomic `ReplaceFile` API; a plain, non-atomic
  move is used only when that API is genuinely unavailable on the current
  platform, never as a silent fallback after a real failure.
- Cancellation (Ctrl+C) is observed between files and at multiple points
  within a single file's conversion; a cancelled run never leaves a
  half-written destination, because the destination is only ever touched by
  the final atomic install step.

## Command-line usage

Run `LineEndingNormalizer.exe -?` (or `-h`, `/?`, `--help`) at any time to
print this from the tool itself.

```
LineEndingNormalizer.exe
    -BasePath <directory>
    [-Include "<pattern1,pattern2,...>"]
    [-Exclude "<pattern1,pattern2,...>"]

    [-Target <CRLF|LF|CR>]        # Default: CRLF. WINDOWS/UNIX/MAC accepted as aliases.

    [-ValidateOnly]                # Read-only: report files that don't match -Target
    [-DetectOnly]                  # Read-only: detect and report encoding/line-ending only

    [-Report <path>]               # Also write a CSV report, in any mode
    [-MaxParallelism <N>]          # Default: min(logical processor count, 4)
    [-WhatIf]                      # Convert mode only: report without writing
    [-Backup]                      # Convert mode only (ignored under -WhatIf): back up before overwriting

    [-Verbose]                     # Show every checked file, including unchanged ones
    [-Quiet]                       # Suppress per-file console output (summary/errors remain)
    [-FullPath]                    # Absolute paths instead of paths relative to -BasePath
    [-Deterministic]               # Process and emit console results in ordinal path order

    [-FailOnChanges]               # Non-zero exit if any file requires (or, under
                                    # -ValidateOnly, fails) conversion
```

### Examples

Convert a subtree to CRLF:

```
LineEndingNormalizer.exe -BasePath C:\Source -Include "*.cs,*.txt" -Target CRLF
```

Nested/path-qualified selection (only files under `src\`, not elsewhere in
the tree):

```
LineEndingNormalizer.exe -BasePath . -Include "src/*.cs" -Target LF
```

Exclude generated files:

```
LineEndingNormalizer.exe -BasePath . -Include "*.cs" -Exclude "*.designer.cs,*.g.cs" -Target CRLF
```

Preview only, nothing is written:

```
LineEndingNormalizer.exe -BasePath . -Include "*" -Target CRLF -WhatIf
```

Validate as a CI gate, without writing anything:

```
LineEndingNormalizer.exe -BasePath . -Include "*" -Target CRLF -ValidateOnly -FailOnChanges -Quiet
```

Read-only detection, CSV to stdout:

```
LineEndingNormalizer.exe -BasePath . -Include "*" -DetectOnly > report.csv
```

Convert with a backup of each changed file:

```
LineEndingNormalizer.exe -BasePath . -Include "*.cs,*.txt" -Target CRLF -Backup
```

Convert and also write a CSV report, in deterministic order:

```
LineEndingNormalizer.exe -BasePath . -Include "*" -Target CRLF -Report report.csv -Deterministic
```

## Pattern semantics

- A pattern with **no** `/` or `\` matches the bare filename, at any
  directory depth (e.g. `-Include "b.cs"` matches `src/nested/b.cs`).
- A pattern **containing** `/` or `\` matches the path relative to
  `-BasePath` instead (e.g. `-Include "src/*.cs"` matches only files under
  `src`).
- `/` and `\` are treated identically in path-qualified patterns.
- `*` and `?` are the only wildcards. Under the current implementation, `*`
  can match across directory separators — there is no distinct `**`
  recursive-glob syntax.
- `-Include` is applied before `-Exclude`.
- The following directories are always skipped during traversal: `.git`,
  `.svn`, `.hg`, `.vs`, `.idea`, `bin`, `obj`, `node_modules`, `packages`,
  `dist`, `build`, `target`.
- `.bak` files and LEN's own temporary conversion files are always excluded
  from matching, regardless of `-Include`/`-Exclude`.

## Modes

| Mode | Flag | Files modified? |
|---|---|---|
| Normalize (default) | *(none)* | Yes — files requiring conversion are rewritten |
| Preview | `-WhatIf` | No — reports what would happen |
| Validate | `-ValidateOnly` | No — reports files not already matching `-Target` as `Invalid` |
| Detect | `-DetectOnly` | No — read-only encoding/line-ending report; `-Target`, `-WhatIf`, `-Backup`, `-FailOnChanges`, `-Quiet`, and `-Verbose` have no effect |

`-DetectOnly` and `-ValidateOnly` cannot be combined with each other.

## Supported charsets

Detection first checks for a Unicode byte-order mark, then a set of
BOM-less Unicode heuristics (`UnicodeDetector.cs`), covering ASCII, UTF-8,
UTF-16LE/BE, and UTF-32LE/BE. If no Unicode encoding is detected,
[UtfUnknown](https://github.com/CharsetDetector/UTF-unknown) is used to
propose a legacy encoding, which is then independently re-verified by
strict decoding and a text-quality check before being trusted
(`TextEncoding.cs`, `TextValidation.cs`).

A detected legacy encoding is only eligible for *conversion* (not merely
detection/reporting) if it passes an additional safety check specific to
LEN's raw-byte normalization path: every single-byte code page is checked
generically (CR/LF must retain their ASCII byte values in that code page),
and multi-byte code pages require explicit verification in a small,
hand-maintained allowlist. See `TextEncoding.IsSafeLegacyEncoding` and the
`SafeMultiByteLegacyCodePages` list in `TextEncoding.cs` for the exact,
current set — it is intentionally conservative and is the authoritative
source rather than being duplicated here.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Clean run. |
| 1 | Invalid command-line arguments. |
| 2 | `-FailOnChanges` was set and at least one file required (or, under `-ValidateOnly`, failed) conversion. |
| 3 | One or more files failed to process, or the `-Report` file could not be written. |
| 4 | The run was cancelled (Ctrl+C). |
| 5 | `-BasePath` directory does not exist. |
| 6 | `-BasePath` is itself a symbolic link, junction, or other reparse point. |

Codes `0`–`4` are identical to
[EncodingChecker](https://github.com/amrali-eg/EncodingChecker)'s, so a script
driving both tools can share one exit-code mapping. Codes `5` and `6` refine
cases EncodingChecker reports as `1`, so no code means two different things
across the two tools — treat `1`, `5`, and `6` alike to handle both.

## Requirements

- Built and tested on Windows, targeting **.NET 10** (`net10.0`).
- Run the framework-dependent build with the
  [.NET 10 Runtime](https://dotnet.microsoft.com/download) installed, or use
  a self-contained build, which bundles its own runtime.
- The Windows-specific atomic-replace path (`ReplaceFile`) is used when
  available; a portable fallback exists in code for when it is not, but only
  the Windows build is currently tested and released.

## Build and test

```
dotnet restore
dotnet build
dotnet test
```

## License

[MPL 2.0](LICENSE). Third-party dependency notices are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
