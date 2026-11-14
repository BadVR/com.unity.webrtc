# Compare matched color probe logs from the same APK and negotiated codec.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineLog,
    [Parameter(Mandatory)][string]$AhbLog,
    [ValidateRange(0,255)][int]$Tolerance = 2
)
$ErrorActionPreference = 'Stop'
$expected = @('black','gray25','gray50','gray75','red','green','blue','cyan','magenta','yellow','skin')
function Get-Samples([string]$Path) {
    $samples = @{}
    $pattern = 'COLOR_SAMPLE name=(\w+) P([01]) pixel=(\d+),(\d+),(\d+),(\d+)'
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match $pattern) {
            $key = "$($Matches[1]):P$($Matches[2])"
            if ($samples.ContainsKey($key)) { throw "Duplicate sample $key in $Path" }
            $samples[$key] = @([int]$Matches[3], [int]$Matches[4], [int]$Matches[5], [int]$Matches[6])
        }
    }
    foreach ($name in $expected) {
        foreach ($peer in 0,1) {
            if (!$samples.ContainsKey("${name}:P$peer")) { throw "Missing sample ${name}:P$peer in $Path" }
        }
    }
    if ($samples.Count -ne $expected.Count * 2) { throw "Unexpected sample count in $Path" }
    return $samples
}
$baseline = Get-Samples $BaselineLog
$ahb = Get-Samples $AhbLog
$maxDifference = 0
foreach ($key in ($baseline.Keys | Sort-Object)) {
    $differences = for ($channel = 0; $channel -lt 4; $channel++) {
        [Math]::Abs($baseline[$key][$channel] - $ahb[$key][$channel])
    }
    $sampleMax = ($differences | Measure-Object -Maximum).Maximum
    $maxDifference = [Math]::Max($maxDifference, $sampleMax)
    Write-Output "$key baseline=$($baseline[$key] -join ',') ahb=$($ahb[$key] -join ',') maxDifference=$sampleMax"
}
if ($maxDifference -gt $Tolerance) { throw "Color comparison failed: maximum difference $maxDifference exceeds $Tolerance." }
Write-Output "COLOR_COMPARISON PASS samples=$($baseline.Count) tolerance=$Tolerance maxDifference=$maxDifference"
