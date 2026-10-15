[CmdletBinding()]
param([switch]$OpenBrowser)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\FinanceForgeLab.Web'
$runtime = Join-Path $root '.private\runtime'
$url = 'http://127.0.0.1:5148'
function Test-Ready {
    try {
        $response = Invoke-WebRequest -Uri ($url + '/Access') -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -eq 200 -and $response.Content.Contains('FinanceForgeLab')
    } catch { return $false }
}
$mutex = New-Object Threading.Mutex($false,'Local\FinanceForgeLab-Start')
$acquired = $false
try {
    $acquired = $mutex.WaitOne(45000)
    if (-not $acquired) { throw 'Outro arranque ainda esta em curso. Tenta novamente.' }
    if (-not (Test-Ready)) {
        if (Get-NetTCPConnection -LocalPort 5148 -State Listen -ErrorAction SilentlyContinue) { throw 'A porta 5148 esta ocupada e nao devolve a aplicacao esperada.' }
        [void][IO.Directory]::CreateDirectory($runtime)
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        $projectFile = Join-Path $project 'FinanceForgeLab.Web.csproj'
        $buildArguments = @('build',$projectFile,'--nologo','--verbosity','quiet')
        $assetsFile = Join-Path $project 'obj\project.assets.json'
        $refreshPaths = $false
        if (Test-Path -LiteralPath $assetsFile) {
            try {
                $cachedProject = (Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json).project.restore.projectPath
                $refreshPaths = -not $cachedProject -or [IO.Path]::GetFullPath($cachedProject) -ne $projectFile
            } catch { $refreshPaths = $true }
        }
        # NuGet and static web assets cache absolute paths. Refresh both
        # after moving/renaming the repository, while preserving .private.
        if ($refreshPaths) {
            & $dotnet restore $projectFile --force --no-logo --verbosity quiet *> (Join-Path $runtime 'build.log')
            if ($LASTEXITCODE -ne 0) { throw 'A atualizacao dos caminhos falhou. Consulta .private\runtime\build.log.' }
            $buildArguments += @('--no-restore','--no-incremental')
        }
        & $dotnet @buildArguments *> (Join-Path $runtime 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'A compilacao falhou. Consulta .private\runtime\build.log.' }
        $dll = Join-Path $project 'bin\Debug\net10.0\FinanceForgeLab.Web.dll'
        $process = Start-Process -FilePath $dotnet -ArgumentList @(('"' + $dll + '"'),'--urls',$url) -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime 'web.stdout.log') -RedirectStandardError (Join-Path $runtime 'web.stderr.log')
        [IO.File]::WriteAllText((Join-Path $runtime 'web.pid'),$process.Id.ToString())
        $deadline = [datetime]::UtcNow.AddSeconds(40)
        while ([datetime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { throw 'O servidor terminou durante o arranque. Consulta .private\runtime\web.stderr.log.' }
            if (Test-Ready) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not (Test-Ready)) { throw 'O servidor ainda nao responde. Consulta os registos em .private\runtime.' }
    }
    Write-Output ('FinanceForgeLab pronto em ' + $url)
    Write-Output 'O servidor continua em segundo plano. Para parar, usa Stop-FinanceForgeLab.cmd.'
    if ($OpenBrowser) { Start-Process $url }
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
