#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [switch]$StagedOnly,
    [ValidateRange(1,100)][int]$MaximumFileMiB = 50
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
function Git-Output([string[]]$CommandArguments) {
    $output = & git -C $ProjectRoot -c core.quotePath=false @CommandArguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($CommandArguments[0]) failed." }
    return $output
}
$files = if ($StagedOnly) { @(Git-Output -CommandArguments @('diff','--cached','--name-only','--diff-filter=ACMR')) }
         else { @(Git-Output -CommandArguments @('ls-files')) }
$failures = [Collections.Generic.List[string]]::new()
$secretPatterns = @{
    'private key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
    'GitHub token' = '\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b'
    'AWS access key' = '\bAKIA[0-9A-Z]{16}\b'
    'embedded credential' = '(?i)\b(?:api[_-]?key|client[_-]?secret|password|access[_-]?token)\s*[:=]\s*["''][A-Za-z0-9+/=_-]{16,}["'']'
}
$totalBytes = 0L
foreach ($relative in $files) {
    $relative = [string]$relative
    if ($relative -match '(?i)(^|/)(bin|obj|publish|release|output|testresults|artifacts|\.vs|\.codex|packages)/|\.(exe|dll|onnx|onnx\.data|pt|pth|ckpt|safetensors|traineddata|pfx|p12|pem|key|log|trx|coverage|nupkg|zip|7z|tar|tar\.gz|tar\.bz2|dmp)$' -or
        ($relative -match '(^|/)\.env($|\.)' -and $relative -notmatch '(^|/)\.env\.example$')) {
        $failures.Add("Do not commit local/binary/private output: $relative")
        continue
    }
    $size = [long](Git-Output -CommandArguments @('cat-file','-s',(':' + $relative)))
    $totalBytes += $size
    if ($size -gt $MaximumFileMiB * 1MB) { $failures.Add("Source file exceeds $MaximumFileMiB MiB: $relative"); continue }
    if ($relative -match '\.(ico|png|jpe?g|gif|webp|woff2?)$') { continue }
    $text = (Git-Output -CommandArguments @('show',(':' + $relative))) -join "`n"
    foreach ($pattern in $secretPatterns.GetEnumerator()) {
        # Report only category and path, never the matching credential value.
        if ([regex]::IsMatch($text,$pattern.Value)) { $failures.Add("Possible $($pattern.Key): $relative") }
    }
}
if ($failures.Count) { throw ("Repository upload checks failed:`n" + ($failures -join "`n")) }
Write-Host ("Verified {0} indexed source/assets/notice files ({1:N2} MiB). No blocked binaries, private-file paths or credential patterns found." -f $files.Count,($totalBytes / 1MB)) -ForegroundColor Green
Write-Host 'This check prepares source review; it does not commit, push, or upload release assets.'