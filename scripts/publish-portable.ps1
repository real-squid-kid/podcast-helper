param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$commit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$') { throw 'Cannot determine build commit.' }
$modified = if (@(& git -C $projectRoot status --porcelain).Count -gt 0) { 'true' } else { 'false' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifactsRoot ('PodcastHelper-win-x64-' + $commit.Substring(0,12) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $destination.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable output must be a new directory inside artifacts/.' }
if (Test-Path -LiteralPath $destination) { throw 'Output already exists. Use a new directory to preserve previous data.' }
$arguments = @('publish', (Join-Path $projectRoot 'src/PodcastHelper.App/PodcastHelper.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PortablePublish=true', '-p:DebugType=None', '-p:DebugSymbols=false', ('-p:PublishDir=' + (Join-Path $destination 'libs') + [IO.Path]::DirectorySeparatorChar), ('-p:SourceRevisionId=' + $commit), ('-p:BuildCommit=' + $commit), ('-p:BuildModified=' + $modified))
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'dev.ps1') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed. The incomplete output is retained for review.' }
$config = Get-Content -LiteralPath (Join-Path $destination 'libs/PodcastHelper.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($config.runtimeOptions.frameworks -or $config.runtimeOptions.framework -or -not $config.runtimeOptions.includedFrameworks) { throw 'Output is not self-contained.' }
foreach ($framework in $config.runtimeOptions.includedFrameworks) { if ($framework.version -ne '10.0.14') { throw 'Runtime version differs from the reviewed legal notices.' } }
foreach ($required in @('PodcastHelper.exe','LICENSE','THIRD_PARTY_NOTICES.md','libs/coreclr.dll','libs/hostfxr.dll','libs/hostpolicy.dll','libs/PresentationFramework.dll','licenses/dotnet-runtime/LICENSE.txt','licenses/dotnet-runtime/THIRD-PARTY-NOTICES.txt','licenses/windows-desktop/LICENSE.txt','licenses/windows-desktop/WPF-NOTICES.txt','licenses/windows-desktop/WINFORMS-NOTICES.txt','buttons/05-back.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $required) -PathType Leaf)) { throw ('Missing distribution file: ' + $required) }
}
'Portable folder: ' + $destination
'Build: ' + $commit.Substring(0,12) + ' modified=' + $modified
'No network publication was performed.'
