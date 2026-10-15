# Windows PowerShell 5.1 + Microsoft ACE OLE DB, with matching process architecture.
# This tool copies the source before connecting. It never opens the original through ACE.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-z0-9][a-z0-9-]{2,79}$')][string]$DatasetId
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$sourcePath = [IO.Path]::GetFullPath($Source)
$outputPath = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'Source not found' }
if (Test-Path -LiteralPath $outputPath) { throw 'Choose a new destination directory' }
[void][IO.Directory]::CreateDirectory($outputPath)
$backupPath = Join-Path $outputPath 'source.accdb'
$inspectionPath = Join-Path $outputPath 'inspection.accdb'
$stream = [IO.File]::Open($sourcePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    $destinationStream = [IO.File]::Open($backupPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.CopyTo($destinationStream) } finally { $destinationStream.Dispose() }
    $length = $stream.Length
    $stream.Position = 0
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $sourceHash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','') } finally { $hasher.Dispose() }
} finally { $stream.Dispose() }
if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Backup hash mismatch' }
Copy-Item -LiteralPath $backupPath -Destination $inspectionPath
(Get-Item -LiteralPath $backupPath).IsReadOnly = $true

function SchemaRows($table) {
    $result = New-Object 'System.Collections.Generic.List[object]'
    foreach ($row in $table.Rows) {
        $record = [ordered]@{}
        foreach ($col in $table.Columns) {
            $value = $row[$col.ColumnName]
            if ($value -is [DBNull]) { $value = $null }
            $record[$col.ColumnName] = $value
        }
        $result.Add([pscustomobject]$record)
    }
    return ,$result.ToArray()
}
function ScalarString($value) {
    if ($value -is [DBNull]) { return $null }
    if ($value -is [datetime]) { return $value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff",[Globalization.CultureInfo]::InvariantCulture) }
    if ($value -is [byte[]]) { return [Convert]::ToBase64String($value) }
    if ($value -is [single] -or $value -is [double]) { return $value.ToString('R',[Globalization.CultureInfo]::InvariantCulture) }
    if ($value -is [IFormattable]) { return $value.ToString($null,[Globalization.CultureInfo]::InvariantCulture) }
    if ($value -is [string] -or $value -is [bool] -or $value -is [guid]) { return $value.ToString() }
    throw ('Unsupported source value type: ' + $value.GetType().FullName)
}
$builder = New-Object System.Data.OleDb.OleDbConnectionStringBuilder
$builder.set_Provider('Microsoft.ACE.OLEDB.16.0')
$builder.set_DataSource([string]$inspectionPath)
$builder.set_Item('Mode','Read')
$connection = New-Object System.Data.OleDb.OleDbConnection($builder.ConnectionString)
try {
    $connection.Open()
    $schema = [ordered]@{}
    foreach($name in @('Tables','Columns','Indexes')) { $schema[$name] = SchemaRows ($connection.GetSchema($name)) }
    foreach($name in @('Primary_Keys','Foreign_Keys')) {
        $guid = [System.Data.OleDb.OleDbSchemaGuid]::$name
        $schema[$name] = SchemaRows ($connection.GetOleDbSchemaTable($guid,$null))
    }
    if (@($schema.Tables | Where-Object { $_.TABLE_TYPE -match 'LINK' }).Count -gt 0) { throw 'Linked tables require an explicit preservation plan' }
    $tables = [ordered]@{}
    foreach ($table in @($schema.Tables | Where-Object { $_.TABLE_TYPE -eq 'TABLE' } | Sort-Object TABLE_NAME)) {
        $name = $table.TABLE_NAME
        $cmd = $connection.CreateCommand()
        $cmd.CommandText = 'SELECT * FROM [' + $name.Replace(']',']]') + '] ORDER BY [ID]'
        $reader = $cmd.ExecuteReader()
        try {
            $rows = New-Object 'System.Collections.Generic.List[object]'
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($i=0; $i -lt $reader.FieldCount; $i++) { $row[$reader.GetName($i)] = ScalarString ($reader.GetValue($i)) }
                $rows.Add([pscustomobject]$row)
            }
            $tables[$name] = $rows.ToArray()
        } finally { $reader.Dispose(); $cmd.Dispose() }
    }
    $package = [ordered]@{ FormatVersion=1; DatasetId=$DatasetId; SourceSha256=$sourceHash; SourceLength=$length; ExporterVersion='1'; Schema=$schema; Tables=$tables }
    $json = $package | ConvertTo-Json -Depth 30 -Compress
    [IO.File]::WriteAllText((Join-Path $outputPath 'package.json'),$json,(New-Object Text.UTF8Encoding($false)))
    $manifest = [ordered]@{ SourceSha256=$sourceHash; PackageSha256=(Get-FileHash -LiteralPath (Join-Path $outputPath 'package.json') -Algorithm SHA256).Hash; DatasetId=$DatasetId; Tables=$tables.Count; Records=(@($tables.Values | ForEach-Object { $_.Count }) | Measure-Object -Sum).Sum; CreatedUtc=[datetime]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText((Join-Path $outputPath 'manifest.json'),($manifest | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    Write-Output ('Export verified: ' + $tables.Count + ' tables. Private package created.')
} finally { $connection.Dispose() }
