# Fails when a private tracker identifier or an internal roadmap label appears in a tracked file.
#
# These are working-notes vocabulary: backlog ids shaped like `IC-BL-NNN`, and the `Phase N` / `Wave N`
# labels an earlier plan used for delivery order. They carry no meaning for a reader of this
# repository, and two of them were baked into frozen PostgreSQL migration comments, where removing
# them later costs a deliberate checksum change (see docs/versioning.md, "Database"). This gate
# keeps a new one from reaching a published file in the first place.
#
# The examples in this header are deliberately written in shapes the patterns below do not match, so
# this file is scanned like every other tracked file and the gate polices itself.
#
# The separator between a label and its number is optional and matching is case-insensitive:
# `Phase N`, `Phase-N`, `Phase_N` and the run-together `PhaseN` are all findings, wherever they
# appear: prose, identifiers, route strings.
#
# Scope is every file `git ls-files` reports, minus the exclusions below.

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$rules = @(
    [pscustomobject]@{
        Rule = "tracker-id"
        Pattern = "IC-BL-?\d+"
    },
    [pscustomobject]@{
        Rule = "phase-label"
        Pattern = "\bPhase[ _-]?\d+"
    },
    [pscustomobject]@{
        Rule = "wave-label"
        Pattern = "\bWave[ _-]?\d+"
    }
)

# Published release notes are historical records: they describe what shipped under the vocabulary in
# use at the time, and rewriting a published page is worse than a label it already contains. This is
# a closed list of the notes that are public today, not a version wildcard: a release note for a
# version that has not shipped yet is scanned like any other file, so a tracker id or phase label
# cannot reach a new release page just by being new. Adding an entry here is a deliberate act, taken
# only for an already-published file that has to quote history verbatim. As of this commit none of
# the four listed files contains a match; the list records the allowance, it is not hiding one.
$excludedPaths = @(
    "docs/release-notes-v0.1.0.md",
    "docs/release-notes-v0.1.1.md",
    "docs/release-notes-v0.2.0.md",
    "docs/release-notes-v0.3.0.md"
)

# Binary assets have no reviewable text; matching raw bytes only produces noise.
$binaryExtensions = @(".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".snk", ".dll", ".exe", ".zip")

Push-Location $repoRoot
try {
    $trackedFiles = & git ls-files
    if ($LASTEXITCODE -ne 0) {
        throw "Internal reference gate: 'git ls-files' failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$findings = New-Object System.Collections.Generic.List[object]
$scannedCount = 0

foreach ($relativePath in $trackedFiles) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        continue
    }

    if ($excludedPaths -contains $relativePath) {
        continue
    }

    $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
    if ($binaryExtensions -contains $extension) {
        continue
    }

    $fullPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        continue
    }

    $text = Get-Content -Raw -LiteralPath $fullPath
    if ($null -eq $text -or $text.Contains([char]0)) {
        continue
    }

    $scannedCount++
    $lines = $text -split "`r?`n"
    for ($index = 0; $index -lt $lines.Length; $index++) {
        foreach ($rule in $rules) {
            foreach ($match in [regex]::Matches($lines[$index], $rule.Pattern, "IgnoreCase")) {
                $findings.Add([pscustomobject]@{
                    Rule = $rule.Rule
                    File = $relativePath
                    Line = $index + 1
                    Detail = $match.Value
                })
            }
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Output "Internal reference gate: no findings in $scannedCount tracked file(s)."
    exit 0
}

Write-Output "Internal reference gate: $($findings.Count) finding(s) in $scannedCount tracked file(s)."
Write-Output "Rename the concept after what it does; do not reference a private tracker or roadmap label."
$findings |
    Sort-Object Rule, File, Line |
    Format-Table Rule, File, Line, Detail -AutoSize -Wrap

exit 1
