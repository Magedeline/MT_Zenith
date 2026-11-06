# Waits for the running MT_Zenith to close, rebuilds it, then reopens the given map.
param([int]$ProcessId, [string]$Map)
$ErrorActionPreference = 'Continue'
$dir = $PSScriptRoot
$log = Join-Path $dir 'rebuild-on-close.log'
"waiting for $ProcessId ($(Get-Date))" | Out-File $log -Encoding utf8
try { Wait-Process -Id $ProcessId -ErrorAction Stop } catch { }
dotnet build (Join-Path $dir 'CELMapViewer.csproj') -c Release 2>&1 | Out-File $log -Append -Encoding utf8
if ($LASTEXITCODE -eq 0) {
    Start-Process (Join-Path $dir 'bin\Release\net8.0-windows\MT_Zenith.exe') -ArgumentList "`"$Map`""
    "relaunched ($(Get-Date))" | Out-File $log -Append -Encoding utf8
} else {
    "BUILD FAILED" | Out-File $log -Append -Encoding utf8
}
