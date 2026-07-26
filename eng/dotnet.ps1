param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArguments
)

$runtimeRoot = 'D:\AM-LINK-Runtime'
$env:DOTNET_CLI_HOME = Join-Path $runtimeRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $runtimeRoot 'nuget-packages'
$env:APPDATA = Join-Path $runtimeRoot 'appdata\roaming'
$env:LOCALAPPDATA = Join-Path $runtimeRoot 'appdata\local'
$env:USERPROFILE = Join-Path $runtimeRoot 'profile'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& (Join-Path $runtimeRoot 'dotnet8-sdk\dotnet.exe') @DotnetArguments
exit $LASTEXITCODE
