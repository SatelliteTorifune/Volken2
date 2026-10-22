<#
check-docs.ps1 - lint docs/ against the maintenance rules in AGENTS.md.

Why it exists: every real incident in this repo's docs was mechanically detectable -
a duplicate file, an orphan markdown table, references left behind after renumbering,
CRLF/BOM introduced by a restore, leftover temp files, paragraph-sized "index" lines.

Usage
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-docs.ps1 [-Root docs]

Exit codes: 0 = all checks pass, 1 = findings, 2 = misuse/IO error.

Checks
  C1 encoding      UTF-8 without BOM, LF only, no U+FFFD
  C2 links         every relative file link and Markdown heading anchor resolves
  C3 local paths   no drive-letter absolute path / machine name outside LOCAL_PATHS.md
  C4 budgets       per-file line/byte budgets and total public-doc budget
  C5 tables        every markdown table has a header + separator row (no orphan rows)
  C6 duplicates    no byte-identical or near-identical (>=90% same lines) document pair
  C7 leftovers     no temp/scratch files (_*.md, *_tmp*, *.tmp, *.bak)
  C8 markers       a circled item marker (1-50) is used as a list item at most once per doc
  C9 git           no index/worktree anomaly (AD = staged add but missing on disk, MD = same)
  C10 structure    required topic files only, no subdirectories, complete README index
  C11 records      no chronology headings, long code/log blocks or growing evidence tables

ASCII-only on purpose: this must also run on PowerShell 5.1, which decodes a BOM-less
UTF-8 script with the ANSI code page.
#>
[CmdletBinding()]
param([string]$Root = 'docs')

$script:fail = 0
function Say([string]$level, [string]$msg) {
    Write-Host ("  [{0}] {1}" -f $level, $msg)
    if ($level -eq 'FAIL') { $script:fail++ }
}
function Ck([string]$name, [int]$findings, [string]$ok) {
    if ($findings -eq 0) { Write-Host ("{0}: PASS - {1}" -f $name, $ok) } else { Write-Host ("{0}: {1} finding(s)" -f $name, $findings) }
}
function Mk([int]$n) {
    if ($n -le 20) { return [char](0x2460 + $n - 1) }
    elseif ($n -le 35) { return [char](0x3251 + $n - 21) }
    else { return [char](0x32B1 + $n - 36) }
}
# Approximates the GitHub heading slug: strip the leading hashes and inline markup,
# lowercase, drop punctuation, spaces -> hyphens. Good enough to catch dead anchors.
function Slug([string]$heading) {
    $t = ($heading -replace '^#+\s*', '').ToLowerInvariant()
    $t = $t -replace '[`*_]', ''
    $t = $t -replace '[^\p{L}\p{Nd}\s\-]', ''
    $t = $t -replace '\s+', '-'
    return $t.Trim('-')
}

if (-not (Test-Path -LiteralPath $Root)) { Write-Error "root not found: $Root"; exit 2 }
$rootFull = (Resolve-Path -LiteralPath $Root).ProviderPath
$allFiles = @(Get-ChildItem -LiteralPath $rootFull -Recurse -Force -File)
$files = @($allFiles | Where-Object { $_.Extension -eq '.md' -or $_.Extension -eq '.ps1' })
if (-not $files) { Write-Error "no .md/.ps1 under $rootFull"; exit 2 }
# Keep this map and the budgets in AGENTS.md in sync: lines, line length, UTF-8 bytes.
$budgets = @{
    'README.md' = @(60, 400, (6 * 1024))
    'AGENT_CONTEXT.md' = @(40, 500, (4 * 1024))
    'architecture.md' = @(120, 1200, (12 * 1024))
    'clouds.md' = @(140, 1200, (16 * 1024))
    'cloud-core.md' = @(100, 1200, (10 * 1024))
    'weather.md' = @(160, 1200, (18 * 1024))
    'development.md' = @(140, 1200, (18 * 1024))
    'backlog.md' = @(160, 1200, (20 * 1024))
}
$totalLineBudget = 600
$totalByteBudget = 64 * 1024
Write-Host ("check-docs: {0} file(s) under {1}" -f $files.Count, $rootFull)
Write-Host ""

# ---- C1 encoding -----------------------------------------------------------
$n1 = 0
foreach ($f in $files) {
    $b = [IO.File]::ReadAllBytes($f.FullName)
    $t = [Text.Encoding]::UTF8.GetString($b)
    $bom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
    $crlf = $t.Contains("`r`n")
    $bad = $t.Contains([char]0xFFFD)
    if ($bom -or $crlf -or $bad) {
        $n1++; Say 'FAIL' ("{0}: BOM={1} CRLF={2} U+FFFD={3}" -f $f.Name, $bom, $crlf, $bad)
    }
}
Ck 'C1 encoding' $n1 'UTF-8 without BOM, LF only, no U+FFFD'

# ---- C2 links --------------------------------------------------------------
$n2 = 0
foreach ($f in $files) {
    if ($f.Extension -ne '.md') { continue }
    $dir = $f.DirectoryName
    $slugs = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($l in [IO.File]::ReadAllLines($f.FullName)) { if ($l -match '^#{1,6} ') { [void]$slugs.Add((Slug $l)) } }
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($f.FullName), '\]\(([^)]+)\)')) {
        $tgt = $m.Groups[1].Value
        if ($tgt -match '^[A-Za-z][A-Za-z0-9+.-]*:') { continue }
        if ($tgt.StartsWith('#')) {
            if (-not $slugs.Contains($tgt.Substring(1))) { $n2++; Say 'FAIL' ("{0}: anchor points at no heading -> {1}" -f $f.Name, $tgt) }
            continue
        }
        $pathPart = $tgt; $anchor = ''
        if ($tgt -match '^(.*?)#(.+)$') { $pathPart = $Matches[1]; $anchor = $Matches[2] }
        $full = [IO.Path]::GetFullPath((Join-Path $dir $pathPart))
        if (-not (Test-Path $full)) { $n2++; Say 'FAIL' ("{0}: dead link -> {1}" -f $f.Name, $tgt); continue }
        if ($anchor -ne '' -and $pathPart -match '\.md$') {
            $slugs2 = New-Object 'System.Collections.Generic.HashSet[string]'
            foreach ($l in [IO.File]::ReadAllLines($full)) { if ($l -match '^#{1,6} ') { [void]$slugs2.Add((Slug $l)) } }
            if (-not $slugs2.Contains($anchor)) { $n2++; Say 'FAIL' ("{0}: cross-file anchor points at no heading -> {1}" -f $f.Name, $tgt) }
        }
    }
}
Ck 'C2 links' $n2 'every relative file link and Markdown #anchor resolves'

# ---- C3 local absolute paths ----------------------------------------------
$n3 = 0
$privateTokens = @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -ge 3 }
foreach ($f in $files) {
    if ($f.Name -eq 'LOCAL_PATHS.md') { continue }
    # tools/ holds scripts: their own source contains the patterns below (regex literals),
    # so scanning them would always self-match. The rule targets documents.
    if ($f.FullName -match '[\\/]tools[\\/]') { continue }
    $i = 0
    foreach ($l in [IO.File]::ReadAllLines($f.FullName)) {
        $i++
        # A drive letter must not be the last letter of an URL scheme (e.g. https://).
        $hasPrivateToken = $false
        foreach ($token in $privateTokens) {
            $pattern = '(?<![\p{L}\p{Nd}_])' + [regex]::Escape($token) + '(?![\p{L}\p{Nd}_])'
            if ($l -match $pattern) { $hasPrivateToken = $true; break }
        }
        if ($l -match '(?<![A-Za-z0-9_])[A-Za-z]:[\\/]' -or $hasPrivateToken) {
            $n3++; Say 'FAIL' ("{0}:{1}: local absolute path - {2}" -f $f.Name, $i, $l.Trim())
        }
    }
}
Ck 'C3 local paths' $n3 'no drive-letter absolute path or machine name (LOCAL_PATHS.md exempt)'

# ---- C4 budgets ------------------------------------------------------------
$globalMax = 1200
$n4 = 0
$publicLines = 0
$publicBytes = 0
foreach ($f in $files) {
    $lines = [IO.File]::ReadAllLines($f.FullName)
    if ($f.Extension -eq '.md' -and $f.Name -ne 'LOCAL_PATHS.md') {
        $publicLines += $lines.Count
        $publicBytes += $f.Length
    }
    $max = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $len = $lines[$i].Length
        if ($len -gt $max) { $max = $len }
        if ($len -gt $globalMax) { $n4++; Say 'FAIL' ("{0}:{1}: line length {2} > {3} (paragraph pasted into a doc?)" -f $f.Name, ($i + 1), $len, $globalMax) }
    }
    if ($budgets.ContainsKey($f.Name)) {
        $maxLines = $budgets[$f.Name][0]; $maxLen = $budgets[$f.Name][1]; $maxBytes = $budgets[$f.Name][2]
        if ($lines.Count -gt $maxLines) { $n4++; Say 'FAIL' ("{0}: {1} lines > budget {2}" -f $f.Name, $lines.Count, $maxLines) }
        if ($max -gt $maxLen) { $n4++; Say 'FAIL' ("{0}: longest line {1} > budget {2}" -f $f.Name, $max, $maxLen) }
        if ($f.Length -gt $maxBytes) { $n4++; Say 'FAIL' ("{0}: {1} UTF-8 bytes > budget {2}" -f $f.Name, $f.Length, $maxBytes) }
    }
}
if ($publicLines -gt $totalLineBudget) { $n4++; Say 'FAIL' ("public docs: {0} lines > total budget {1}" -f $publicLines, $totalLineBudget) }
if ($publicBytes -gt $totalByteBudget) { $n4++; Say 'FAIL' ("public docs: {0} bytes > total budget {1}" -f $publicBytes, $totalByteBudget) }
Ck 'C4 budgets' $n4 ("per-file budgets met; public docs {0}/{1} lines, {2}/{3} bytes" -f $publicLines, $totalLineBudget, $publicBytes, $totalByteBudget)

# ---- C5 tables -------------------------------------------------------------
$n5 = 0
foreach ($f in $files) {
    if ($f.Extension -ne '.md') { continue }
    $lines = [IO.File]::ReadAllLines($f.FullName)
    $inFence = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].Trim()
        if ($t.StartsWith('```')) { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        if (-not $t.StartsWith('|')) { continue }
        # start of a table block?
        $prev = ''
        for ($j = $i - 1; $j -ge 0; $j--) { if ($lines[$j].Trim() -ne '') { $prev = $lines[$j].Trim(); break } }
        if ($prev.StartsWith('|')) { continue }
        # this is the first row: it must be followed by a separator row
        $next = ''
        if (($i + 1) -lt $lines.Count) { $next = $lines[$i + 1].Trim() }
        if ($next -notmatch '^\|[\s:\-\|]+\|$') { $n5++; Say 'FAIL' ("{0}:{1}: table starts without header/separator row (orphan rows)" -f $f.Name, ($i + 1)) }
    }
}
Ck 'C5 tables' $n5 'every table has a header + separator row'

# ---- C6 duplicates --------------------------------------------------------
$n6 = 0
$docs = @($files | Where-Object { $_.Extension -eq '.md' })
$sets = @{}
foreach ($f in $docs) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($l in [IO.File]::ReadAllLines($f.FullName)) { $x = $l.Trim(); if ($x -ne '') { [void]$set.Add($x) } }
    $sets[$f.FullName] = $set
}
for ($ia = 0; $ia -lt $docs.Count; $ia++) {
    for ($ib = $ia + 1; $ib -lt $docs.Count; $ib++) {
        # NOTE: PowerShell variables are case-insensitive - naming these $A/$B would
        # clobber the loop indices $a/$b and silently skip the whole comparison.
        $setA = $sets[$docs[$ia].FullName]
        $setB = $sets[$docs[$ib].FullName]
        if ($setA.Count -lt 30 -or $setB.Count -lt 30) { continue }
        $hit = 0
        foreach ($x in $setA) { if ($setB.Contains($x)) { $hit++ } }
        $ratio = $hit / [Math]::Min($setA.Count, $setB.Count)
        if ($ratio -ge 0.90) { $n6++; Say 'FAIL' ("near-duplicate documents ({0:P0} identical lines): {1} <-> {2}" -f $ratio, $docs[$ia].Name, $docs[$ib].Name) }
    }
}
Ck 'C6 duplicates' $n6 'no byte-identical or >=90%-identical document pair'

# ---- C7 leftovers ---------------------------------------------------------
$n7 = 0
foreach ($f in $files) {
    if ($f.Name -like '_*' -or $f.Name -like '*_tmp*' -or $f.Extension -eq '.tmp' -or $f.Extension -eq '.bak') {
        $n7++; Say 'FAIL' ("{0}: looks like a scratch/temp file" -f $f.FullName.Replace($rootFull, '').TrimStart('\'))
    }
}
Ck 'C7 leftovers' $n7 'no temp/scratch file left behind'

# ---- C8 item markers ------------------------------------------------------
$n8 = 0
foreach ($f in $files) {
    if ($f.Extension -ne '.md') { continue }
    # scope = innermost ## or ### heading: the same marker may legitimately restart
    # in a different section, but must not repeat inside one.
    $counts = @{}
    $scope = '(none)'
    foreach ($l in [IO.File]::ReadAllLines($f.FullName)) {
        $t = $l.Trim()
        if ($t -match '^#{2,3} ') { $scope = $t; $counts = @{}; continue }
        if ($t.Length -lt 3 -or -not $t.StartsWith('**')) { continue }
        $c = $t.Substring(2, 1)
        for ($k = 1; $k -le 50; $k++) {
            if ([string]$c -eq [string](Mk $k)) {
                $counts[$k] = 1 + [int]$counts[$k]
                if ($counts[$k] -gt 1) { $n8++; Say 'FAIL' ("{0}: item marker {1} used {2} times inside {3}" -f $f.Name, (Mk $k), $counts[$k], $scope) }
                break
            }
        }
    }
}
Ck 'C8 markers' $n8 'each circled item marker used at most once per document'

# ---- C9 git ---------------------------------------------------------------
$n9 = 0
$gitTop = $null
try { $gitTop = (& git -C $rootFull rev-parse --show-toplevel 2>$null) } catch { $gitTop = $null }
if ($gitTop) {
    $st = & git -C $rootFull status --short -- . 2>$null
    foreach ($line in $st) {
        if ($line -match '^(AD|MD)\s') { $n9++; Say 'FAIL' ("index/worktree anomaly ({0}): {1}" -f $line.Substring(0, 2), $line.Substring(3)) }
    }
} else {
    Write-Host "C9 git: SKIP - not inside a git work tree"
}
if ($gitTop) { Ck 'C9 git' $n9 'no AD/MD (staged file missing on disk) anomaly' }

# ---- C10 fixed topic structure --------------------------------------------
$n10 = 0
foreach ($dir in @(Get-ChildItem -LiteralPath $rootFull -Recurse -Force -Directory)) {
    $n10++; Say 'FAIL' ("{0}: docs subdirectories are not allowed; update an existing topic" -f $dir.FullName.Substring($rootFull.Length + 1))
}
foreach ($f in $allFiles) {
    if ($f.DirectoryName -ne $rootFull -or (-not $budgets.ContainsKey($f.Name) -and $f.Name -ne 'LOCAL_PATHS.md')) {
        $n10++; Say 'FAIL' ("{0}: not an allowed docs file; use the topic ownership in AGENTS.md" -f $f.FullName.Substring($rootFull.Length + 1))
    }
}
foreach ($name in $budgets.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $rootFull $name) -PathType Leaf)) {
        $n10++; Say 'FAIL' ("missing required topic: {0}" -f $name)
    }
}
$indexPath = Join-Path $rootFull 'README.md'
if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
    $indexText = [IO.File]::ReadAllText($indexPath)
    foreach ($name in $budgets.Keys) {
        if ($name -eq 'README.md') { continue }
        $pattern = '\]\((?:\./)?' + [regex]::Escape($name) + '(?:#[^)]*)?\)'
        if ($indexText -notmatch $pattern) { $n10++; Say 'FAIL' ("README.md does not index {0}" -f $name) }
    }
}
Ck 'C10 structure' $n10 'only required topics and optional LOCAL_PATHS.md; README indexes all topics'

# ---- C11 concise final-state records --------------------------------------
# Unicode escapes keep this script ASCII-compatible with Windows PowerShell 5.1.
$chronologyHeading = '^#{2,6}\s+.*(?:\d{4}-\d{2}-\d{2}|\bround\s+\d+|\u7b2c\s*\d+\s*\u8f6e|(?:\u5b9e\u65bd|\u6392\u67e5|\u66f4\u65b0|\u53d8\u66f4|\u4fee\u590d)\u8bb0\u5f55|\u540e\u7eed\u8865\u5145)'
$evidenceHeading = '^##\s+\u5df2\u6709\u9a8c\u8bc1\u8bc1\u636e\s*$'
$n11 = 0
foreach ($f in $files) {
    if ($f.Extension -ne '.md' -or $f.Name -eq 'LOCAL_PATHS.md') { continue }
    $inFence = $false; $fenceLines = 0; $fenceStart = 0; $closingFence = ''
    $inEvidence = $false; $evidenceHeaderSeen = $false; $evidenceRows = 0; $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
        $lineNo++; $trimmed = $line.Trim()
        if ($inFence) {
            if ($trimmed -match $closingFence) {
                if ($fenceLines -gt 20) { $n11++; Say 'FAIL' ("{0}:{1}: example block has {2} lines > 20; keep full output outside docs" -f $f.Name, $fenceStart, $fenceLines) }
                $inFence = $false
            } else { $fenceLines++ }
            continue
        }
        if ($trimmed -match '^(`{3,}|~{3,})') {
            $marker = $Matches[1]
            $closingFence = '^' + [regex]::Escape($marker.Substring(0, 1)) + '{' + $marker.Length + ',}\s*$'
            $inFence = $true; $fenceLines = 0; $fenceStart = $lineNo
            continue
        }
        if ($trimmed -match $chronologyHeading) { $n11++; Say 'FAIL' ("{0}:{1}: chronology heading; replace the current topic section instead of appending history" -f $f.Name, $lineNo) }
        if ($f.Name -eq 'development.md') {
            if ($trimmed -match '^#{1,2}\s') { $inEvidence = $trimmed -match $evidenceHeading }
            if ($inEvidence -and $trimmed.StartsWith('|') -and $trimmed -notmatch '^\|[\s:\-\|]+\|$') {
                if ($evidenceHeaderSeen) { $evidenceRows++ } else { $evidenceHeaderSeen = $true }
            }
        }
    }
    if ($inFence) { $n11++; Say 'FAIL' ("{0}:{1}: unclosed example block" -f $f.Name, $fenceStart) }
    if ($evidenceRows -gt 10) { $n11++; Say 'FAIL' ("{0}: {1} evidence rows > 10; replace obsolete results" -f $f.Name, $evidenceRows) }
}
Ck 'C11 records' $n11 'no chronology headings; example blocks <=20 lines; evidence rows <=10'

Write-Host ""
if ($script:fail -eq 0) { Write-Host 'RECAP: all checks passed.'; exit 0 }
Write-Host ("RECAP: {0} finding(s) - fix the content; change AGENTS.md and this script together only when topic ownership changes." -f $script:fail)
exit 1
