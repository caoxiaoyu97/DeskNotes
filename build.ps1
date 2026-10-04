param([switch]$Run, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (-not $SkipTests) {
    & $dotnet run --project (Join-Path $root 'tests\DeskNotes.Tests\DeskNotes.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Storage tests failed.' }
}
<#
Self-contained so the target machine needs no .NET install, but deliberately NOT single-file:
the runtime sits next to the exe instead of being extracted to a temp folder on every launch.
dist\config and dist\data are left untouched by the publish.
#>
& $dotnet publish (Join-Path $root 'src\DeskNotes.App\DeskNotes.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $root 'dist')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$exe = Join-Path $root 'dist\DeskNotes.exe'
Write-Host "Built: $exe"
if ($Run) { Start-Process $exe }
