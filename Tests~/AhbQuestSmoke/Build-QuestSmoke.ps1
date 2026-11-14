#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [ValidateRange(1,100)][int]$Cycles = 3,
    [ValidateRange(64,4096)][int]$VideoWidth = 1280,
    [ValidateRange(64,4096)][int]$VideoHeight = 720,
    [ValidatePattern('^[a-zA-Z0-9-]{1,48}$')][string]$RunLabel = 'ahb-spike',
    [string]$OutputDirectory,
    [string]$NativeAar
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$UnityEditor = (Resolve-Path -LiteralPath $UnityEditor).Path
$androidTools = Join-Path (Split-Path $UnityEditor) 'Data\PlaybackEngines\AndroidPlayer'
$java = Join-Path $androidTools 'OpenJDK\bin\java.exe'
$gradleLauncher = @(Get-ChildItem -LiteralPath (Join-Path $androidTools 'Tools\gradle\lib') -Filter 'gradle-launcher-*.jar')
if (!(Test-Path -LiteralPath $java -PathType Leaf) -or $gradleLauncher.Count -ne 1) {
    throw 'The selected Unity editor needs its Android SDK, NDK, OpenJDK and Gradle modules installed.'
}
if (!$NativeAar) { $NativeAar = Join-Path $repoRoot 'Runtime\Plugins\Android\libwebrtc.aar' }
$NativeAar = (Resolve-Path -LiteralPath $NativeAar).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts~\ahb-validation' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$RunLabel-$stamp"))
$work = Join-Path ([IO.Path]::GetTempPath()) ('BWQ-' + [Guid]::NewGuid().ToString('N').Substring(0,12))
$project = Join-Path $work 'project'
$package = Join-Path $work 'package'
foreach ($dir in @($output,$package,(Join-Path $project 'Assets\Scripts'),(Join-Path $project 'Assets\Editor'),
    (Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings'))) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
# Mirror only the package needed by this app. Tests would duplicate our friend
# assembly; build caches and dependency headers must never become Unity assets.
foreach ($entry in @('Runtime','Editor','package.json','Runtime.meta','Editor.meta','package.json.meta')) {
    $source = Join-Path $repoRoot $entry
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $package -Recurse -Force }
}
Copy-Item -LiteralPath $NativeAar -Destination (Join-Path $package 'Runtime\Plugins\Android\libwebrtc.aar') -Force
foreach ($file in @('QuestLoopbackSmoke.cs','QuestRegressionChecks.cs','AhbNativeProbe.cs','QuestSmoke.asmdef')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $project 'Assets\Scripts')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'QuestSmokeBuild.cs') -Destination (Join-Path $project 'Assets\Editor')
$harnessPath = Join-Path $project 'Assets\Scripts\QuestLoopbackSmoke.cs'
$harness = Get-Content -LiteralPath $harnessPath -Raw
$cyclePattern = '(?m)^    const int DefaultCycleCount = \d+;'
if ([regex]::Matches($harness,$cyclePattern).Count -ne 1) { throw 'Expected one CycleCount constant in the owned harness.' }
$harness = [regex]::Replace($harness,$cyclePattern,"    const int DefaultCycleCount = $Cycles;")
$resolutionPattern = '(?m)^    const int DefaultVideoWidth = \d+, DefaultVideoHeight = \d+;'
if ([regex]::Matches($harness,$resolutionPattern).Count -ne 1) { throw 'Expected one default resolution declaration.' }
if ($VideoWidth % 2 -or $VideoHeight % 2) { throw 'H264 dimensions must be even.' }
[regex]::Replace($harness,$resolutionPattern,"    const int DefaultVideoWidth = $VideoWidth, DefaultVideoHeight = $VideoHeight;") | Set-Content -LiteralPath $harnessPath -Encoding utf8
@{ dependencies = [ordered]@{
    'com.unity.webrtc' = 'file:' + $package.Replace('\','/')
    'com.unity.modules.audio' = '1.0.0'
    'com.unity.modules.androidjni' = '1.0.0'
    'com.unity.modules.imgui' = '1.0.0'
    'com.unity.modules.jsonserialize' = '1.0.0'
} } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Encoding utf8
$unityVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($UnityEditor).ProductVersion.Split('_')[0]
"m_EditorVersion: $unityVersion" | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Encoding utf8

function Get-Fingerprints([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/');
            bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Invoke-LoggedProcess([string]$Path,[string[]]$Arguments,[string]$WorkingDirectory,[string]$LogFile,[hashtable]$Environment) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Path
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) {
        if ($null -eq $Environment[$key]) { $info.EnvironmentVariables.Remove($key) }
        else { $info.EnvironmentVariables[$key] = $Environment[$key] }
    }
    $process = [Diagnostics.Process]::Start($info)
    $stream = [IO.File]::Open($LogFile,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
    try {
        $stdout = $process.StandardOutput.BaseStream.CopyToAsync($stream)
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $null = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
    } finally { $stream.Dispose() }
    # Do not persist JVM option echoes; callers may have sensitive Java options.
    $echoPattern = '(?m)^Picked up JAVA_TOOL_OPTIONS:.*(?:\r?\n|$)'
    [IO.File]::WriteAllText("$LogFile.stderr.log",[regex]::Replace($errorText,$echoPattern,''))
    $outputText = [IO.File]::ReadAllText($LogFile)
    if ([regex]::IsMatch($outputText,$echoPattern)) { [IO.File]::WriteAllText($LogFile,[regex]::Replace($outputText,$echoPattern,'')) }
    if ($process.ExitCode -ne 0) { throw "$Path exited $($process.ExitCode); see $LogFile and $LogFile.stderr.log" }
    $process.Dispose()
}

$commit = $null
$worktreeStatus = @()
if (Get-Command git -ErrorAction SilentlyContinue) {
    $commit = (& git -C $repoRoot rev-parse HEAD) -join ''
    $worktreeStatus = @(& git -C $repoRoot status --porcelain)
}
$fingerprints = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o'); unityVersion = $unityVersion; cycles = $Cycles; videoWidth = $VideoWidth; videoHeight = $VideoHeight; codec = "H264"; applicationId = "com.badvr.webrtcahb.spike"
    sourceCommit = $commit; sourceWorktreeStatus = $worktreeStatus
    project = $project; mirroredPackage = $package; nativeArchiveSource = $NativeAar
    nativeArchiveSha256 = (Get-FileHash -LiteralPath (Join-Path $package 'Runtime\Plugins\Android\libwebrtc.aar') -Algorithm SHA256).Hash
    packageFiles = Get-Fingerprints $package; harnessFiles = Get-Fingerprints (Join-Path $project 'Assets')
}
$fingerprintPath = Join-Path $output 'fingerprints.json'
$fingerprints | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fingerprintPath -Encoding utf8
Write-Output "Project: $project"
Write-Output "Output: $output"
Write-Output 'Exporting Android Gradle project with Unity...'
Invoke-LoggedProcess $UnityEditor @('-batchmode','-nographics','-projectPath',$project,'-buildTarget','Android',
    '-executeMethod','QuestSmokeBuild.Build','-logFile',(Join-Path $output 'unity-export.log')) $project `
    (Join-Path $output 'unity-console.log') @{ JAVA_TOOL_OPTIONS = $null }
$gradleProject = Join-Path $project 'Builds\QuestGradleProject'
if (!(Test-Path -LiteralPath (Join-Path $gradleProject 'settings.gradle'))) { throw 'Unity did not export the expected Gradle project.' }
# JDK's Unix-domain listener falls back to TCP when bind fails. This child must
# remain absent. Environment inheritance reaches both launcher and daemon;
# putting this only in Gradle JVM arguments does not fix the launcher's socket.
$missingSocketDirectory = (Join-Path $work 'intentionally-missing-jvm-socket-dir').Replace('\','/')
$javaEnvironment = @{
    JAVA_HOME = (Join-Path $androidTools 'OpenJDK')
    ANDROID_HOME = (Join-Path $androidTools 'SDK')
    ANDROID_SDK_ROOT = (Join-Path $androidTools 'SDK')
    JAVA_TOOL_OPTIONS = '-Djdk.net.unixdomain.tmpdir="' + $missingSocketDirectory + '"'
}
Write-Output 'Packaging APK with the selected editor OpenJDK and Gradle...'
Invoke-LoggedProcess $java @('-Dorg.gradle.jvmargs=-Xmx4096m','-classpath',$gradleLauncher[0].FullName,
    'org.gradle.launcher.GradleMain','--no-daemon','--console=plain','assembleDebug') $gradleProject `
    (Join-Path $output 'gradle.log') $javaEnvironment
$builtApk = Join-Path $gradleProject 'launcher\build\outputs\apk\debug\launcher-debug.apk'
$apk = Join-Path $output 'QuestWebRTCAhbSpike.apk'
Copy-Item -LiteralPath $builtApk -Destination $apk
$fingerprints['completedUtc'] = [DateTime]::UtcNow.ToString('o')
$fingerprints['apkSha256'] = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash
$fingerprints | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fingerprintPath -Encoding utf8
Write-Output "APK=$apk"
Write-Output "FINGERPRINTS=$fingerprintPath"
