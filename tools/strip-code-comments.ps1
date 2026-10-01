<#
strip-code-comments.ps1 - prove a code change touched ONLY comments.

Why it exists: the comment rules (docs/README.md section 5.11) forbid changing any
code while cleaning comments. `git diff -w` still shows trailing-comment edits on
code lines, so this script removes comments and trivia, then compares what is left.

Usage
  # compare old vs new (files or directories); exit 0 = code identical, 1 = code differs
  pwsh -NoProfile -File strip-code-comments.ps1 -Old <path> -New <path>

  # dump the code-only lines of one file or tree (eyeballing / piping)
  pwsh -NoProfile -File strip-code-comments.ps1 -Path <path>

Handles: // /* */  "..."  @"..." (with "" escape)  $"..."  $@"..."  'c'
Trivia removed: comment bodies, #region/#endregion names, leading/trailing and
repeated whitespace, blank lines. Kept: everything else, including directives.

Deliberately ASCII-only: this must also run on PowerShell 5.1, which decodes a
BOM-less UTF-8 script with the ANSI code page; non-ASCII here would garble output.
#>
[CmdletBinding()]
param(
    [string]$Old,
    [string]$New,
    [string]$Path,
    [string]$Filter = '*.cs,*.shader,*.compute,*.hlsl,*.cginc,*.h'
)

function Get-CodeLines {
    param([string]$Text)
    $raw = New-Object 'System.Collections.Generic.List[string]'
    $line = New-Object System.Text.StringBuilder
    $i = 0
    $n = $Text.Length
    while ($i -lt $n) {
        $c = $Text[$i]
        # line comment
        if ($c -eq '/' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '/') {
            while ($i -lt $n -and $Text[$i] -ne "`n") { $i++ }
            continue
        }
        # block comment
        if ($c -eq '/' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '*') {
            $i += 2
            while (($i + 1) -lt $n -and -not ($Text[$i] -eq '*' -and $Text[$i + 1] -eq '/')) {
                if ($Text[$i] -eq "`n") { [void]$raw.Add($line.ToString()); [void]$line.Clear() }
                $i++
            }
            $i += 2
            continue
        }
        # verbatim string @"..." ; also entered for $@"..."
        if ($c -eq '@' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '"') {
            [void]$line.Append('@"')
            $i += 2
            while ($i -lt $n) {
                if ($Text[$i] -eq '"') {
                    if (($i + 1) -lt $n -and $Text[$i + 1] -eq '"') { [void]$line.Append('""'); $i += 2; continue }
                    [void]$line.Append('"'); $i++; break
                }
                if ($Text[$i] -eq "`n") { [void]$raw.Add($line.ToString()); [void]$line.Clear() }
                else { [void]$line.Append($Text[$i]) }
                $i++
            }
            continue
        }
        # normal or interpolated string "..." / $"..."
        if ($c -eq '"' -or ($c -eq '$' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '"')) {
            if ($c -eq '$') { [void]$line.Append('$'); $i++ }
            [void]$line.Append('"'); $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq '\') {
                    [void]$line.Append($Text[$i])
                    if (($i + 1) -lt $n) { [void]$line.Append($Text[$i + 1]) }
                    $i += 2
                    continue
                }
                if ($Text[$i] -eq '"') { [void]$line.Append('"'); $i++; break }
                if ($Text[$i] -eq "`n") { [void]$raw.Add($line.ToString()); [void]$line.Clear() }
                else { [void]$line.Append($Text[$i]) }
                $i++
            }
            continue
        }
        # char literal
        if ($c -eq "'") {
            [void]$line.Append("'"); $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq '\') {
                    [void]$line.Append($Text[$i])
                    if (($i + 1) -lt $n) { [void]$line.Append($Text[$i + 1]) }
                    $i += 2
                    continue
                }
                if ($Text[$i] -eq "'") { [void]$line.Append("'"); $i++; break }
                [void]$line.Append($Text[$i])
                $i++
            }
            continue
        }
        if ($c -eq "`n") { [void]$raw.Add($line.ToString()); [void]$line.Clear(); $i++; continue }
        if ($c -eq "`r") { $i++; continue }
        [void]$line.Append($c)
        $i++
    }
    [void]$raw.Add($line.ToString())

    $code = New-Object 'System.Collections.Generic.List[string]'
    foreach ($l in $raw) {
        $t = ($l -replace '\s+', ' ').Trim()
        if ($t -eq '') { continue }
        $t = $t -replace '^#region\b.*', '#region'
        $t = $t -replace '^#endregion\b.*', '#endregion'
        $code.Add($t)
    }
    return , $code
}

# Report lines go to Write-Host on purpose: this function must return ONLY the
# boolean, otherwise the caller would capture @(report, $false) - always truthy.
function Compare-Lists {
    param([string]$Label, $A, $B)
    if ($A.Count -eq $B.Count) {
        $same = $true
        for ($k = 0; $k -lt $A.Count; $k++) { if ($A[$k] -ne $B[$k]) { $same = $false; break } }
        if ($same) { Write-Host "  IDENTICAL  $Label  ($($A.Count) code lines)"; return $true }
    }
    Write-Host "  DIFFERS    $Label  (old $($A.Count) vs new $($B.Count) code lines)"
    $max = [Math]::Max($A.Count, $B.Count)
    for ($k = 0; $k -lt $max; $k++) {
        $a = if ($k -lt $A.Count) { $A[$k] } else { '<none>' }
        $b = if ($k -lt $B.Count) { $B[$k] } else { '<none>' }
        if ($a -ne $b) {
            Write-Host "    first difference at code line $($k + 1):"
            Write-Host "      old: $a"
            Write-Host "      new: $b"
            break
        }
    }
    return $false
}

function Resolve-Input([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Error "path not found: $p"; exit 2 }
    return (Resolve-Path -LiteralPath $p).ProviderPath
}

if ($Path) {
    $target = Resolve-Input $Path
    if (Test-Path -LiteralPath $target -PathType Container) {
        $patterns = $Filter.Split(',')
        Get-ChildItem -LiteralPath $target -Recurse -File -Include $patterns | Sort-Object FullName | ForEach-Object {
            $rel = $_.FullName.Substring($target.Length).TrimStart('\')
            Write-Output "=== $rel ==="
            foreach ($l in (Get-CodeLines ([IO.File]::ReadAllText($_.FullName)))) { Write-Output $l }
        }
    }
    else {
        foreach ($l in (Get-CodeLines ([IO.File]::ReadAllText($target)))) { Write-Output $l }
    }
    exit 0
}

if (-not $Old -or -not $New) {
    Write-Error "usage: -Old <path> -New <path>  |  -Path <path>"
    exit 2
}

$o = Resolve-Input $Old
$n = Resolve-Input $New
$oIsDir = Test-Path -LiteralPath $o -PathType Container
$nIsDir = Test-Path -LiteralPath $n -PathType Container
$allSame = $true

if ($oIsDir -and $nIsDir) {
    $patterns = $Filter.Split(',')
    $oFiles = Get-ChildItem -LiteralPath $o -Recurse -File -Include $patterns
    $nFiles = Get-ChildItem -LiteralPath $n -Recurse -File -Include $patterns
    $oMap = @{}; foreach ($f in $oFiles) { $oMap[$f.FullName.Substring($o.Length).TrimStart('\')] = $f.FullName }
    $nMap = @{}; foreach ($f in $nFiles) { $nMap[$f.FullName.Substring($n.Length).TrimStart('\')] = $f.FullName }

    $onlyOld = $oMap.Keys | Where-Object { -not $nMap.ContainsKey($_) } | Sort-Object
    $onlyNew = $nMap.Keys | Where-Object { -not $oMap.ContainsKey($_) } | Sort-Object
    foreach ($k in $onlyOld) { Write-Output "  ONLY-OLD   $k"; $allSame = $false }
    foreach ($k in $onlyNew) { Write-Output "  ONLY-NEW   $k"; $allSame = $false }

    foreach ($k in ($oMap.Keys | Where-Object { $nMap.ContainsKey($_) } | Sort-Object)) {
        $a = Get-CodeLines ([IO.File]::ReadAllText($oMap[$k]))
        $b = Get-CodeLines ([IO.File]::ReadAllText($nMap[$k]))
        if (-not (Compare-Lists $k $a $b)) { $allSame = $false }
    }
}
elseif (-not $oIsDir -and -not $nIsDir) {
    $a = Get-CodeLines ([IO.File]::ReadAllText($o))
    $b = Get-CodeLines ([IO.File]::ReadAllText($n))
    $allSame = Compare-Lists ([IO.Path]::GetFileName($n)) $a $b
}
else {
    Write-Error "OLD and NEW must both be files or both be directories"
    exit 2
}

if ($allSame) { Write-Output "RESULT: code is identical - the change touched only comments/trivia."; exit 0 }
Write-Output "RESULT: code differs - this change is NOT comment-only."
exit 1
