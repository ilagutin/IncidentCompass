param(
    [int] $MaxFileLines = 400,
    [int] $MaxLogicalTypeLines = 400,
    [int] $MaxTestFileLines = 800
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$srcRoot = Join-Path $repoRoot "src"
$testsRoot = Join-Path $repoRoot "tests"

function Get-RelativePath {
    param([string] $Path)

    return Resolve-Path -Relative $Path
}

function Get-FileLineCount {
    param([string] $Path)

    return (Get-Content -LiteralPath $Path).Count
}

function Get-LineNumber {
    param(
        [string] $Text,
        [int] $Index
    )

    return ($Text.Substring(0, $Index) -split "`n").Count
}

function Get-FileNamespace {
    param([string] $Text)

    $namespaceMatch = [regex]::Match($Text, "(?m)^\s*namespace\s+([^;\r\n{]+)")
    if ($namespaceMatch.Success) {
        return $namespaceMatch.Groups[1].Value.Trim()
    }

    return "<global>"
}

function Get-ScannedFiles {
    param([string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        return @()
    }

    return Get-ChildItem -Path $Root -Recurse -Filter "*.cs" |
        Where-Object {
            $_.FullName -notmatch "[/\\]bin[/\\]" -and
            $_.FullName -notmatch "[/\\]obj[/\\]" -and
            $_.FullName -notmatch "[/\\]TestResults[/\\]"
        }
}

# Only column-zero declarations are logical types. Every namespace in this repository is
# file-scoped, so a nested type is always indented; matching indented declarations would count a
# nested helper as a second type in its file, cut the enclosing type's measured span short at the
# nested declaration, and merge same-named nested helpers from different files into one bogus
# aggregate. `record class` and `record struct` are matched before the bare keywords so the
# captured name is the type name rather than the word `class` or `struct`.
$topLevelTypePattern = "(?m)^(?:(?:public|internal|file)\s+)?(?:(?:sealed|abstract|static|partial|readonly|ref|unsafe)\s+)*(?:record\s+(?:class|struct)|class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)"
$nestedPrivateTypePattern = "(?m)^\s+private\s+(?:(?:sealed|abstract|static|partial|readonly)\s+)*(?:class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)"
$statusStringPattern = '"(Passed|Succeeded|Failed|Running|Canceled|TimedOut|Rejected|ValidationFailed|ApprovalRequired|NotExecuted|NotRequired|SimulatedApproved|Valid|Invalid)"'

$productionFiles = Get-ScannedFiles $srcRoot |
    Where-Object { $_.FullName -notmatch "[/\\]tests?[/\\]" }
$testFiles = Get-ScannedFiles $testsRoot

# The nested-private-type and status-string rules are production design rules. Test code
# legitimately declares private nested test doubles and asserts on the persisted status
# representation, so applying either rule to `tests/` reports non-violations.
$scopes = @(
    [pscustomobject]@{
        Name = "production"
        Files = $productionFiles
        FileLineLimit = $MaxFileLines
        TypeLineLimit = $MaxLogicalTypeLines
        ApplyDesignRules = $true
    },
    [pscustomobject]@{
        Name = "test"
        Files = $testFiles
        FileLineLimit = $MaxTestFileLines
        TypeLineLimit = $MaxTestFileLines
        ApplyDesignRules = $false
    }
)

$findings = New-Object System.Collections.Generic.List[object]

foreach ($scope in $scopes) {
    $typeFiles = @{}

    foreach ($file in $scope.Files) {
        $text = Get-Content -Raw -LiteralPath $file.FullName
        $lineCount = Get-FileLineCount $file.FullName
        $relativePath = Get-RelativePath $file.FullName

        if ($lineCount -gt $scope.FileLineLimit) {
            $findings.Add([pscustomobject]@{
                Rule = "file-lines"
                File = $relativePath
                Type = ""
                Line = ""
                Detail = "$lineCount lines; $($scope.Name) limit $($scope.FileLineLimit)"
            })
        }

        $namespace = Get-FileNamespace $text
        $typeMatches = [regex]::Matches($text, $topLevelTypePattern)
        for ($index = 0; $index -lt $typeMatches.Count; $index++) {
            $match = $typeMatches[$index]
            $startLine = Get-LineNumber $text $match.Index
            $endLine = $lineCount
            if ($index + 1 -lt $typeMatches.Count) {
                $endLine = (Get-LineNumber $text $typeMatches[$index + 1].Index) - 1
            }

            $spanLineCount = [Math]::Max(1, $endLine - $startLine + 1)
            $fullTypeName = "$namespace.$($match.Groups[1].Value)"
            if (-not $typeFiles.ContainsKey($fullTypeName)) {
                $typeFiles[$fullTypeName] = New-Object System.Collections.Generic.List[object]
            }

            $typeFiles[$fullTypeName].Add([pscustomobject]@{
                File = $relativePath
                Lines = $spanLineCount
            })
        }

        if (-not $scope.ApplyDesignRules) {
            continue
        }

        foreach ($match in [regex]::Matches($text, $nestedPrivateTypePattern)) {
            $lineNumber = Get-LineNumber $text $match.Index
            $findings.Add([pscustomobject]@{
                Rule = "nested-private-type"
                File = $relativePath
                Type = $match.Groups[1].Value
                Line = $lineNumber
                Detail = "private nested type candidate"
            })
        }

        # A documentation comment is prose, not a persisted representation. `<param name="Failed">`
        # is the shape that collides: the rule looks for a status written as a bare string literal in
        # code, and a gate that fires on the documentation of such a field teaches people to rename
        # the field rather than to stop writing the literal.
        $statusMatches = Select-String -LiteralPath $file.FullName -Pattern $statusStringPattern -AllMatches |
            Where-Object { $_.Line.TrimStart() -notmatch '^///' }
        foreach ($statusMatch in $statusMatches) {
            foreach ($match in $statusMatch.Matches) {
                $findings.Add([pscustomobject]@{
                    Rule = "status-string-candidate"
                    File = $relativePath
                    Type = ""
                    Line = $statusMatch.LineNumber
                    Detail = $match.Value
                })
            }
        }
    }

    foreach ($entry in $typeFiles.GetEnumerator()) {
        $totalLines = ($entry.Value | Measure-Object Lines -Sum).Sum
        if ($totalLines -gt $scope.TypeLineLimit) {
            $findings.Add([pscustomobject]@{
                Rule = "logical-type-lines"
                File = ($entry.Value.File -join "; ")
                Type = $entry.Key
                Line = ""
                Detail = "$totalLines aggregate type-span lines; $($scope.Name) limit $($scope.TypeLineLimit)"
            })
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Output "Code organization gate: no findings."
    exit 0
}

Write-Output "Code organization gate: $($findings.Count) finding(s)."
$findings |
    Sort-Object Rule, File, Type, Line |
    Format-Table Rule, File, Type, Line, Detail -AutoSize -Wrap

exit 1
