[CmdletBinding()]
param([switch]$InstallTask, [switch]$OpenBrowser)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$private = Join-Path $root '.private\remote-access'
$config = Join-Path $private 'tunnel.yml'
$settingsFile = Join-Path $private 'settings.json'
$taskName = 'FinanceForgeLab Remote Access'
if (-not (Test-Path -LiteralPath $config) -or -not (Test-Path -LiteralPath $settingsFile)) {
    throw 'O acesso externo ainda nao foi configurado. Consulta docs/acesso-remoto.md.'
}
$settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
if ($settings.PublicOrigin -notmatch '^https://[a-z0-9][a-z0-9.-]+\.[a-z]{2,}/?$' -or $settings.TunnelId -notmatch '^[a-f0-9-]{36}$') {
    throw 'Configuracao de acesso externo invalida.'
}
$cloudflared = (Get-Command cloudflared.exe -ErrorAction Stop).Source
if ($InstallTask) {
    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existing -and -not ($existing.Actions.Arguments -like ('*' + $PSCommandPath + '*'))) {
        throw 'Ja existe uma tarefa com este nome que nao pertence a esta pasta.'
    }
    $action = New-ScheduledTaskAction -Execute (Get-Command powershell.exe).Source `
        -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"') -WorkingDirectory $root
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $taskSettings.Priority = 4
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal `
        -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $currentUser) -Settings $taskSettings `
        -Description 'FinanceForgeLab: app local e tunel HTTPS independente, sem alterar o Gateway.' -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    if ($OpenBrowser) { Start-Process $settings.PublicOrigin }
    Write-Output ('Acesso externo com arranque automatico: ' + $settings.PublicOrigin)
    return
}
$mutex = New-Object Threading.Mutex($false, 'Local\FinanceForgeLab-RemoteAccess')
$acquired = $false
$tunnelProcess = $null
try {
    $acquired = $mutex.WaitOne(0)
    if (-not $acquired) { return }
    & (Join-Path $PSScriptRoot 'Start-FinanceForgeLab.ps1')
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'A aplicacao nao arrancou.' }
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $tunnelProcess = Start-Process -FilePath $cloudflared -ArgumentList @('tunnel', '--config', ('"' + $config + '"'), '--no-autoupdate', 'run', $settings.TunnelId) `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $private ($stamp + '.stdout.log')) -RedirectStandardError (Join-Path $private ($stamp + '.stderr.log'))
    [IO.File]::WriteAllText((Join-Path $private 'tunnel.pid'), $tunnelProcess.Id.ToString())
    $tunnelProcess.WaitForExit()
    if ($tunnelProcess.ExitCode -ne 0) { throw 'O tunel terminou; consulta os registos em .private\remote-access.' }
} finally {
    if ($tunnelProcess -and -not $tunnelProcess.HasExited) { $tunnelProcess.Kill() }
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
