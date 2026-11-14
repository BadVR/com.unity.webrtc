#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Adb,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Serial,
    [string]$Apk,
    [ValidateSet(0,3)][int]$AhbMode = 3,
    [switch]$ExpectFallback,
    [ValidateSet('H264','VP8')][string]$VideoCodec = 'H264',
    [ValidateRange(1,100)][int]$Cycles = 3,
    [ValidateRange(64,4096)][int]$VideoWidth = 1280,
    [ValidateRange(64,4096)][int]$VideoHeight = 720,
    [switch]$EnableTransforms,
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunLabel = 'ahb-spike',
    [string]$LogDirectory,
    [ValidateRange(10,3600)][int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$smokePackage = 'com.badvr.webrtcahb.spike'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$LogDirectory) { $LogDirectory = Join-Path $repoRoot 'artifacts~\ahb-validation' }
$Adb = (Resolve-Path -LiteralPath $Adb).Path
if ($VideoWidth % 2 -or $VideoHeight % 2) { throw 'H264 dimensions must be even.' }
$deviceState = (& $Adb -s $Serial get-state) -join ''
if ($LASTEXITCODE -ne 0 -or $deviceState.Trim() -ne 'device') { throw "Device $Serial is unavailable or unauthorized." }
if ($Apk) {
    $Apk = (Resolve-Path -LiteralPath $Apk).Path
    & $Adb -s $Serial install -r $Apk
    if ($LASTEXITCODE -ne 0) { throw 'Spike APK installation failed.' }
}
$originalMode = ((& $Adb -s $Serial shell getprop debug.ahb.mode) -join '').Trim()
if ($LASTEXITCODE -ne 0 -or $originalMode -notmatch '^-?\d*$') { throw 'Unable to safely capture previous debug.ahb.mode.' }
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
$smokeStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$smokeLog = Join-Path $LogDirectory "$RunLabel-$smokeStamp-utc.log"
$smokeErrors = Join-Path $LogDirectory "$RunLabel-$smokeStamp-adb-errors.log"
$smokeMemory = [Collections.Generic.List[object]]::new()
$metadata = [ordered]@{startedUtc=[DateTime]::UtcNow.ToString('o');applicationId=$smokePackage;serial=$Serial;
    ahbMode=$AhbMode;previousAhbMode=$originalMode;cycles=$Cycles;videoWidth=$VideoWidth;videoHeight=$VideoHeight;
    maxVideoFps=30;codec=$VideoCodec;transforms=[bool]$EnableTransforms;scope='2D-package-loopback';
    apk=$Apk;apkSha256=if($Apk){(Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash}else{$null}}
$metadataPath = Join-Path $LogDirectory "$RunLabel-$smokeStamp-run.json"
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
$smokeCapture = $null
try {
    & $Adb -s $Serial shell am force-stop $smokePackage | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to stop the dedicated spike app.' }
    & $Adb -s $Serial shell setprop debug.ahb.mode $AhbMode
    if ($LASTEXITCODE -ne 0) { throw 'Unable to set requested debug.ahb.mode.' }
    $actualMode = ((& $Adb -s $Serial shell getprop debug.ahb.mode) -join '').Trim()
    if ($actualMode -ne [string]$AhbMode) { throw "AHB mode property mismatch: $actualMode" }
    $smokeCapture = Start-Process -FilePath $Adb -ArgumentList @('-s',$Serial,'logcat','-v','threadtime','-T','1',
        'Unity:V','WebRTC:V','rtc:V','AhbH264:V','AndroidRuntime:E','libc:F','DEBUG:F','*:S') -WindowStyle Hidden -RedirectStandardOutput $smokeLog -RedirectStandardError $smokeErrors -PassThru
    Write-Output "LOG_PATH=$smokeLog"
    $transformFlag = if($EnableTransforms){1}else{0}
    $pathMode = if($ExpectFallback){0}else{$AhbMode}
    $vp8Flag = if($VideoCodec -eq 'VP8'){1}else{0}
    & $Adb -s $Serial shell am start -W -n "$smokePackage/com.unity3d.player.UnityPlayerGameActivity" `
        --ei ahb_mode $AhbMode --ei ahb_path_mode $pathMode --ei ahb_vp8 $vp8Flag --ei ahb_cycles $Cycles --ei ahb_width $VideoWidth --ei ahb_height $VideoHeight --ei ahb_transforms $transformFlag
    if ($LASTEXITCODE -ne 0) { throw 'Unable to launch the dedicated spike app.' }
    $smokeDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $smokeNextMemory = [DateTime]::UtcNow
    while ([DateTime]::UtcNow -lt $smokeDeadline -and -not $smokeCapture.HasExited) {
        Start-Sleep -Seconds 2
        if ([DateTime]::UtcNow -ge $smokeNextMemory) {
            $smokeMemText = (& $Adb -s $Serial shell dumpsys meminfo $smokePackage 2>$null) -join "`n"
            $smokePss = [regex]::Match($smokeMemText, 'TOTAL PSS:\s*(\d+)')
            $smokeRss = [regex]::Match($smokeMemText, 'TOTAL RSS:\s*(\d+)')
            if ($smokePss.Success) {
                $smokeMemory.Add([pscustomobject]@{utc=[DateTime]::UtcNow.ToString('o');pssKB=[int64]$smokePss.Groups[1].Value;rssKB=if($smokeRss.Success){[int64]$smokeRss.Groups[1].Value}else{$null}})
            }
            $smokeNextMemory = [DateTime]::UtcNow.AddSeconds(10)
        }
        $smokeStarts = @(Select-String -LiteralPath $smokeLog -Pattern 'QUEST_SMOKE START platform=' -ErrorAction SilentlyContinue)
        $smokeFinal = @()
        if ($smokeStarts.Count) {
            $smokeFinal = @(Select-String -LiteralPath $smokeLog -Pattern 'QUEST_SMOKE FINAL (PASS|FAIL)' -ErrorAction SilentlyContinue |
                Where-Object LineNumber -GT $smokeStarts[-1].LineNumber)
        }
        if ($smokeFinal) { Write-Output $smokeFinal[-1].Line; break }
        if (Select-String -LiteralPath $smokeLog -Pattern 'Fatal signal|FATAL EXCEPTION' -Quiet -ErrorAction SilentlyContinue) { break }
    }
} finally {
    if ($smokeCapture) {
        if (-not $smokeCapture.HasExited) { $smokeCapture.Kill(); $smokeCapture.WaitForExit(5000) | Out-Null }
        $smokeCapture.Dispose()
    }
    # Restore exactly the previous property (including empty).
    & $Adb -s $Serial shell "setprop debug.ahb.mode '$originalMode'"
    $metadata['restorePropertyExitCode'] = $LASTEXITCODE
    $metadata['completedUtc'] = [DateTime]::UtcNow.ToString('o')
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    $smokeMemory | Export-Csv -LiteralPath (Join-Path $LogDirectory "$RunLabel-$smokeStamp-memory.csv") -NoTypeInformation -Encoding utf8
}
Select-String -LiteralPath $smokeLog -Pattern 'QUEST_REGRESSION PASS|QUEST_SMOKE (START|FINAL|CYCLE [0-9]+ (BEGIN|MEDIA PASS|DISPOSED)|UPLOAD_|PATH_COUNTERS|PIXEL_|RECEIVE_TEXTURE|H264_CAPABILITIES)|Fatal signal|FATAL EXCEPTION' | ForEach-Object { $_.Line }
Write-Output "Log capture complete: $smokeLog"
$smokeStarts = @(Select-String -LiteralPath $smokeLog -Pattern 'QUEST_SMOKE START platform=')
$smokeFinal = @()
if ($smokeStarts.Count) {
    $smokeFinal = @(Select-String -LiteralPath $smokeLog -Pattern 'QUEST_SMOKE FINAL (PASS|FAIL)' |
        Where-Object LineNumber -GT $smokeStarts[-1].LineNumber)
}
if (!$smokeFinal.Count -or $smokeFinal[-1].Line -notmatch 'QUEST_SMOKE FINAL PASS' -or
    (Select-String -LiteralPath $smokeLog -Pattern 'Fatal signal|FATAL EXCEPTION' -Quiet)) {
    throw 'Spike run failed, crashed, or timed out without a fresh FINAL PASS; inspect its log.'
}
