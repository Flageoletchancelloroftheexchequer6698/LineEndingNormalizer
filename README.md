# LineEndingNormalizer

LineEndingNormalizer (LEN) is a Windows command-line tool that detects a
text file's encoding and line-ending style, and normalizes its line endings
to CRLF, LF, or CR while preserving the file's encoding, byte-order mark,
and metadata.

LEN never changes a file's encoding — only its line endings. To convert
between encodings, see the companion tool
[EncodingChecker](https://github.com/amrali-eg/EncodingChecker), which
detects, validates, and converts text encodings from a Windows GUI or
command line; its
[latest release](https://github.com/amrali-eg/EncodingChecker/releases/latest)
publishes framework-dependent and self-contained Windows builds, the same two
shapes as LEN's. The pair share a deliberately compatible
[exit-code scheme](#exit-codes) so one CI script can drive both.

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
  (`ScanEngine.cs`, `LosslessFileWriter.cs`). Both paths reach a codec only for
  the six code pages `TextEncoding.IsUnicodeEncoding` admits, each verified to
  honour that assignment; `TextValidation`, which is handed arbitrary legacy
  encodings, rebuilds the encoding with its fallbacks supplied up front
  (`TextEncoding.Strict`) because the assignment alone is silently ignored for
  `CodePagesEncodingProvider` encodings — see the
  [independent audit](#independent-audit).
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

## Independent audit

The detection pipeline LEN shares with
[EncodingChecker](https://github.com/amrali-eg/EncodingChecker) is audited against
four public corpora — **5,078 files** — by a separate harness:
**[CorpusTesters](https://github.com/amrali-eg/CorpusTesters)**.

Ground truth comes from each corpus's own manifest or catalogue, never from
filenames. Source corpora are treated as read-only and verified untouched after
every run against their published SHA-256 hashes.

### Why LEN's exposure is narrower than a detector score suggests

LEN **never changes a file's encoding**, and for legacy files it never decodes
them at all. That removes most of the failure modes a detection score implies:

| Input | What LEN does | Data-loss surface |
|---|---|---|
| Unicode | Strict decode → normalize → re-encode in the **same** encoding → hash-verify | Only the strict decode, which rejects malformed input rather than replacing it. No codec-mismatch failure mode: the re-encode targets the encoding the file already had. |
| Legacy | Raw byte scan. Only `0x0D`/`0x0A` are ever rewritten; every other byte is copied through untouched | None from codecs — nothing is decoded or re-encoded |

The byte path is gated, not assumed. Single-byte encodings are verified at
runtime to map `0x0D`/`0x0A` to CR/LF, which makes byte scanning provably safe
when every byte is one character. Multi-byte encodings must appear in an explicit
allowlist — Shift-JIS, EUC-KR, EUC-JP, GBK, GB18030, Big5 — each verified not to
emit `0x0D`/`0x0A` inside a multi-byte sequence. Anything else is treated as
undetected and left alone (`TextEncoding.IsSafeLegacyEncoding`).

The practical consequence: **naming the wrong single-byte code page does not
change which bytes are CR or LF**, so a misdetection that would corrupt a file
under an encoding converter is normally harmless here. LEN's safety margin comes
from doing less, not from a better detector — it is the same detector.

### Detection results

Scored per encoding class, micro-averaged over every class the detector can
claim:

| Corpus | Files | Accuracy | FPR | FNR |
|---|---:|---:|---:|---:|
| [UnicodeTestSuite v3.0](https://github.com/amrali-eg/UnicodeTestSuite) | 1,359 | **96.62%** | 0.11% | 2.23% |
| [chardet `test-data`](https://github.com/chardet/test-data) | 3,137 | **89.26%** | 0.46% | 10.77% |

On UnicodeTestSuite the errors have a favourable shape: all 17 false positives
are `Binary` fixtures — files the corpus declares as not-text — and all 29 false
negatives were reported as *undetected* rather than as a wrong encoding. The
detector never mislabels a real text file as the wrong encoding on that corpus,
and declining is the safe failure, since LEN leaves what it cannot name untouched.

### What it found

The audit found that assigning `Decoder.Fallback` after `GetDecoder()` is
silently ignored for `CodePagesEncodingProvider` encodings, which left
`TextValidation` confirming codecs that could not actually read the file. Fixed
in v1.4.0 ([#9](https://github.com/amrali-eg/LineEndingNormalizer/pull/9)).

`LosslessFileWriter` and `ScanEngine` were **not** affected: both reach a decoder
only behind `TextEncoding.IsUnicodeEncoding`, and all six code pages on that
whitelist do honour the assignment — verified empirically rather than assumed.
That makes the whitelist load-bearing, so the test suite now asserts both halves
of it: every admitted code page honours the assigned fallback, and legacy code
pages are rejected by the guard.

### Scope

The audit measures EncodingChecker's converter end to end; it does **not**
exercise LEN's writer. The two share a detector, so the detection results above
apply to both, but LEN's normalization path is covered by its own test suite and
by the structural argument above rather than by these corpus runs.

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
