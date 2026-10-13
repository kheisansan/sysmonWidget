$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

Push-Location $PSScriptRoot
try {
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /out:SysmonWidget.exe `
        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        SysmonWidget.cs
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
    Write-Host "OK: $(Join-Path $PSScriptRoot 'SysmonWidget.exe')"
} finally {
    Pop-Location
}
