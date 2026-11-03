$DotnetArguments = @($args)
if ($DotnetArguments.Count -gt 0 -and $DotnetArguments[0] -in @('build', 'restore', 'publish', 'test')) {
    # Предсказуемый однопроцессный MSBuild без оставшихся фоновых узлов.
    $DotnetArguments += @('/m:1', '/nr:false', '/p:UseSharedCompilation=false')
}
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $localDotnet)) {
    $localDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

# Настройки действуют только во время команды, без изменения среды пользователя.
$variableNames = @('DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_USE_MSBUILD_SERVER')
$previousValues = @{}
foreach ($variableName in $variableNames) {
    $previousValues[$variableName] = [Environment]::GetEnvironmentVariable($variableName, 'Process')
}
try {
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget/packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    Push-Location $projectRoot
    try {
        & $localDotnet @DotnetArguments
        $commandExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally {
    foreach ($variableName in $variableNames) {
        [Environment]::SetEnvironmentVariable($variableName, $previousValues[$variableName], 'Process')
    }
}
exit $commandExitCode
