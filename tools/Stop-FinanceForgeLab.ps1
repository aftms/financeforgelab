$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pidFile = Join-Path $root '.private\runtime\web.pid'
if (-not (Test-Path -LiteralPath $pidFile)) { Write-Output 'Nao existe um servidor gerido pelo lancador.'; exit 0 }
$serverId = [int](Get-Content -LiteralPath $pidFile -Raw)
$process = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $serverId)
$expectedDll = Join-Path $root 'src\FinanceForgeLab.Web\bin\Debug\net10.0\FinanceForgeLab.Web.dll'
if ($process) {
    if (-not $process.CommandLine.Contains($expectedDll) -or -not $process.CommandLine.Contains('127.0.0.1:5148')) { throw 'O processo guardado nao corresponde a este servidor. Nenhum processo foi terminado.' }
    # Only the exact process recorded and validated above is stopped.
    & taskkill.exe /PID $serverId /F | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel terminar o servidor.' }
}
Remove-Item -LiteralPath $pidFile
Write-Output 'FinanceForgeLab parado.'
