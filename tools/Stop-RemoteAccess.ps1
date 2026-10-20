$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$expectedConfig = Join-Path $root '.private\remote-access\tunnel.yml'
$pidFile = Join-Path $root '.private\remote-access\tunnel.pid'
$task = Get-ScheduledTask -TaskName 'FinanceForgeLab Remote Access' -ErrorAction SilentlyContinue
if ($task) {
    if (-not ($task.Actions.Arguments -like ('*' + (Join-Path $PSScriptRoot 'Start-RemoteAccess.ps1') + '*'))) {
        throw 'A tarefa nao pertence a esta pasta; nada foi parado.'
    }
    Stop-ScheduledTask -InputObject $task
}
if (Test-Path -LiteralPath $pidFile) {
    $tunnelId = [int](Get-Content -LiteralPath $pidFile -Raw)
    $process = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $tunnelId)
    if ($process) {
        if (-not $process.CommandLine.Contains($expectedConfig) -or $process.Name -ne 'cloudflared.exe') {
            throw 'O processo guardado nao corresponde ao tunel deste projeto; nada foi terminado.'
        }
        Stop-Process -Id $tunnelId
    }
    Remove-Item -LiteralPath $pidFile
}
Write-Output 'Acesso externo parado. A aplicacao e os dados continuam disponiveis localmente.'
