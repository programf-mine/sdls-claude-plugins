#### SdoTelegramHub 빌드 + 자체 시험. 사용 : powershell -ExecutionPolicy Bypass -File build.ps1 [-Configuration Release] [-MSBuild <경로>]
param(
    [string]$Configuration = 'Release',
    [string]$MSBuild = ''
)
$ErrorActionPreference = 'Stop'
if (-not $MSBuild)
{
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) { $MSBuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
}
if (-not $MSBuild -or -not (Test-Path $MSBuild)) { throw 'MSBuild.exe 를 찾지 못했다 - -MSBuild 로 경로를 줄 것' }
$proj = Join-Path $PSScriptRoot 'src\SdoTelegramHub\SdoTelegramHub.csproj'
& $MSBuild $proj /p:Configuration=$Configuration /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
$exe = Join-Path $PSScriptRoot "src\SdoTelegramHub\bin\$Configuration\SdoTelegramHub.exe"
$out = Join-Path $env:TEMP 'SdoTelegramHub_selftest'
$p = Start-Process -FilePath $exe -ArgumentList '--selftest', ('"{0}"' -f $out) -Wait -PassThru -WindowStyle Hidden
Get-Content (Join-Path $out 'selftest.txt') -Encoding UTF8 | Where-Object { $_ -notmatch '^\[PASS\]' }
if ($p.ExitCode -ne 0) { throw "selftest failed ($($p.ExitCode))" }
Write-Output "OK : $exe"
