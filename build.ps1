$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "csc.exe not found" }

Push-Location $PSScriptRoot
try {
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu /out:SysmonWidget.exe `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        /r:System.Runtime.Serialization.dll /r:System.Security.dll `
        SysmonWidget.cs GmailClient.cs
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
    Write-Host "OK: $(Join-Path $PSScriptRoot 'SysmonWidget.exe')"
} finally {
    Pop-Location
}
