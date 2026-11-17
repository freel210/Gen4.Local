[CmdletBinding()]
param(
    [int] $BatchSize,
    [switch] $Reset
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appHost = Join-Path $repoRoot 'Gen4.Local\Gen4.Local.csproj'
$cliProject = 'C:\Shared\Repos\gen4.hp.master.cli\src\Gen4.HP.Master.CLI'

$hisConnectionString = 'Host=localhost;Username=postgres;Password=postgres;Database=his-db'
$mongoConnectionString = 'mongodb://root:Passw0rd123@localhost:27018'
$mongoDatabase = 'Lyra3CoreDb'

if ($Reset) {
    aspire stop

    $volumes = docker volume ls -q --filter 'name=gen4.local-' | Where-Object { $_ -match '-(postgres|mongo)-data$' }
    foreach ($volume in $volumes) {
        docker volume rm $volume
    }

    Write-Host "Volumes removed. Start the AppHost again: aspire start --apphost `"$appHost`" --non-interactive"
    return
}

$commands = @(
    '--import-lyra3-patients',
    '--import-lyra3-doctors',
    '--import-lyra3-operating-rooms',
    '--import-lyra3-planned-surgeries'
)

$optional = @()
if ($BatchSize -gt 0) {
    $optional = @('--batch-size', $BatchSize)
}

$exitCode = 0

foreach ($command in $commands) {
    Write-Host "== $command $($optional -join ' ')".TrimEnd()

    $arguments = @('run', '--project', $cliProject, '--', $command, $hisConnectionString, $mongoConnectionString, $mongoDatabase) + $optional
    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        Write-Host "exit code: $LASTEXITCODE"
        $exitCode = 1
    }
}

exit $exitCode
