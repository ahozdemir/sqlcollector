<#
.SYNOPSIS
  SqlDataCollector developer / test environment helper (Windows PowerShell 5.1 or PowerShell 7).

.DESCRIPTION
  Local test topology (default):
    * Your locally installed SQL Server -> metadata DB (SdcMeta, also holds the embedded Hangfire tables)
                                          + target DB (SdcDwh), Windows authentication (Integrated Security)
    * Docker: src-erp (localhost,14331), src-crm (localhost,14332) demo sources filled with test data
    * Docker: fakegcs (http://localhost:4443) Google Cloud Storage emulator, bucket "test-bucket"
    * The app runs on the host: dotnet run --project src/SqlDataCollector --launch-profile LocalTest -> http://localhost:5080

.EXAMPLE
  .\scripts\dev.ps1 setup                 # first time: sources + fake GCS, local DBs, schema, metadata seed, appsettings.LocalTest.json
  .\scripts\dev.ps1 run                   # start the app (Web + Worker, embedded Hangfire)
  .\scripts\dev.ps1 run-job 'extract all' # run jobs in-process without Hangfire ('extract 3', 'export all' ...)
  .\scripts\dev.ps1 smoke                 # end-to-end check: all extracts, simulate, incremental, all-tables export, GCS listing
  .\scripts\dev.ps1 simulate              # new/changed rows in ERP and CRM (incremental tests)
  .\scripts\dev.ps1 gcs-ls exports/       # list exported objects
  .\scripts\dev.ps1 test                  # all tests (unit + Testcontainers integration)
  .\scripts\dev.ps1 full-up | shared-up   # everything in Docker (embedded / shared Hangfire deployment)
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'up', 'reseed', 'reset-local', 'run', 'run-job', 'smoke', 'simulate', 'gcs-ls', 'gcs-get',
                 'test', 'test-unit', 'test-integration', 'full-up', 'shared-up', 'down', 'status', 'logs', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$Arg,

    # Local SQL Server instance; auto-detected when empty (MSSQLSERVER -> localhost, MSSQL$NAME -> localhost\NAME)
    [string]$SqlInstance = $env:SDC_SQL_INSTANCE,
    [string]$MetaDb = 'SdcMeta',
    [string]$DwhDb = 'SdcDwh',
    # Optional SQL login instead of Windows authentication (non-Windows hosts / CI)
    [string]$SqlUser = $env:SDC_SQL_USER,
    [string]$SqlPassword = $env:SDC_SQL_PASSWORD,
    [int]$Orders = 100,
    # down: also remove Docker volumes (demo data, emulator storage)
    [switch]$Volumes
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $Root 'docker/compose.yml'
$SaPassword = if ($env:MSSQL_SA_PASSWORD) { $env:MSSQL_SA_PASSWORD } else { 'Dev_Passw0rd!2026' }
$AppProject = Join-Path $Root 'src/SqlDataCollector'
$LocalSettings = Join-Path $AppProject 'appsettings.LocalTest.json'
$DevAdminHash = 'pbkdf2-sha256$210000$8fZnvLJhw+4CmgH8AoG41Q==$SqxhLyhy/XQNROz6zOaGre0B5Tq+vE9dmDdHTn5ctTk='   # admin / Admin!Dev2026 (dev only)
$GcsUrl = 'http://localhost:4443'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Invoke-Compose([string[]]$ComposeArgs) {
    & docker compose -f $ComposeFile @ComposeArgs
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($ComposeArgs -join ' ') failed (exit $LASTEXITCODE)" }
}

function Invoke-Docker([string[]]$DockerArgs) {
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) { throw "docker $($DockerArgs -join ' ') failed (exit $LASTEXITCODE)" }
}

function Get-LocalSqlInstance {
    if ($SqlInstance) { return $SqlInstance }
    if ($IsLinux -or $IsMacOS) { throw 'Pass -SqlInstance (and -SqlUser/-SqlPassword) on non-Windows hosts.' }
    $services = Get-Service -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*' } |
        Sort-Object { $_.Name -ne 'MSSQLSERVER' }
    $running = $services | Where-Object Status -eq 'Running' | Select-Object -First 1
    if (-not $running) { throw 'No running local SQL Server service found (MSSQLSERVER / MSSQL$*). Start it or pass -SqlInstance.' }
    if ($running.Name -eq 'MSSQLSERVER') { return 'localhost' }
    return 'localhost\' + $running.Name.Substring(6)
}

function Get-ConnectionString([string]$Instance, [string]$Database) {
    $auth = if ($SqlUser) { "User ID=$SqlUser;Password=$SqlPassword" } else { 'Integrated Security=true' }
    "Server=$Instance;Database=$Database;$auth;TrustServerCertificate=true;Encrypt=true;Application Name=SqlDataCollector-dev"
}

function Invoke-SqlScript([string]$ConnectionString, [string]$Script) {
    Add-Type -AssemblyName System.Data
    $conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $conn.Open()
    try {
        foreach ($batch in [regex]::Split($Script, '(?im)^\s*GO\s*;?\s*$')) {
            if ([string]::IsNullOrWhiteSpace($batch)) { continue }
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = $batch
            $cmd.CommandTimeout = 600
            [void]$cmd.ExecuteNonQuery()
        }
    }
    finally { $conn.Dispose() }
}

function Wait-Healthy([string[]]$Containers, [int]$TimeoutSec = 240) {
    Write-Step "Waiting for $($Containers -join ', ') to become healthy"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    foreach ($c in $Containers) {
        while ($true) {
            $state = (& docker inspect -f '{{.State.Health.Status}}' $c 2>$null)
            if ($state -eq 'healthy') { break }
            if ((Get-Date) -gt $deadline) { throw "$c did not become healthy (state: $state). See: docker logs $c" }
            Start-Sleep -Seconds 3
        }
    }
}

function Start-Sources([switch]$Reseed) {
    Write-Step 'Starting Docker demo sources (src-erp, src-crm), the GCS emulator (fakegcs) and the SMTP sink (mailpit)'
    $env:RESEED = if ($Reseed) { '1' } else { '0' }
    try {
        Invoke-Compose @('up', '-d', 'src-erp', 'src-crm', 'fakegcs', 'mailpit')
        Wait-Healthy @('sqlcollector-src-erp-1', 'sqlcollector-src-crm-1')
        Write-Step 'Seeding demo data (first run takes about a minute)'
        Invoke-Compose @('up', '--no-deps', '--force-recreate', 'src-seed')
    }
    finally { Remove-Item Env:RESEED -ErrorAction SilentlyContinue }
    $code = (& docker inspect -f '{{.State.ExitCode}}' sqlcollector-src-seed-1)
    if ($code -ne '0') { throw "src-seed failed (exit $code). See: docker logs sqlcollector-src-seed-1" }
}

function Invoke-RunJob([string[]]$JobArgs) {
    # in-process run without Hangfire, using appsettings.LocalTest.json (launch profile LocalTest)
    & dotnet run --project $AppProject --launch-profile LocalTest -- run-job @JobArgs
    if ($LASTEXITCODE -ne 0) { throw "run-job $($JobArgs -join ' ') failed (exit $LASTEXITCODE)" }
}

function Initialize-Local {
    $instance = Get-LocalSqlInstance
    Write-Step "Local SQL Server: $instance  (metadata + embedded Hangfire: $MetaDb, target: $DwhDb)"
    Invoke-SqlScript (Get-ConnectionString $instance 'master') @"
IF DB_ID(N'$MetaDb') IS NULL CREATE DATABASE [$MetaDb];
IF DB_ID(N'$DwhDb') IS NULL CREATE DATABASE [$DwhDb];
"@
    # read-only user that export reads/previews switch to (Collector:ExportReadAsUser); the local login is sysadmin,
    # so no IMPERSONATE grant is needed here (production: see sql/01_permissions_template.sql)
    Invoke-SqlScript (Get-ConnectionString $instance $DwhDb) @"
IF DATABASE_PRINCIPAL_ID(N'sdc_export_reader') IS NULL CREATE USER [sdc_export_reader] WITHOUT LOGIN;
ALTER ROLE db_datareader ADD MEMBER [sdc_export_reader];
"@
    $meta = Get-ConnectionString $instance $MetaDb

    Write-Step 'Installing the etl metadata schema'
    & dotnet run --project $AppProject -- metadata-install $meta
    if ($LASTEXITCODE -ne 0) { throw 'metadata-install failed' }

    Write-Step 'Seeding servers, sample extract jobs and the nightly all-tables export'
    $admin = if ($SqlUser -or -not $env:USERNAME) { '' } else { "$env:USERDOMAIN\$env:USERNAME" }
    $seed = Get-Content (Join-Path $Root 'docker/seed/metadata.sql') -Raw
    $seed = $seed.Replace('__ERP__', 'localhost,14331').Replace('__CRM__', 'localhost,14332').Replace('__DWH__', $instance)
    $seed = $seed.Replace('__DWHDB__', $DwhDb).Replace('__DWH_TRUST__', '1').Replace('__ADMIN__', $admin)
    Invoke-SqlScript $meta $seed

    Write-Step "Writing $LocalSettings"
    $overrides = [ordered]@{ ERP = "User ID=sa;Password=$SaPassword"; CRM = "User ID=sa;Password=$SaPassword" }
    if ($SqlUser) { $overrides['DWH'] = "User ID=$SqlUser;Password=$SqlPassword" }
    $settings = [ordered]@{
        Urls              = 'http://localhost:5080'
        ConnectionStrings = [ordered]@{ Metadata = $meta; Hangfire = '' }   # empty Hangfire = embedded (tables in the metadata DB)
        Collector         = [ordered]@{
            Roles = 'Web,Worker'; Queues = 'sdc_export,sdc_extract,sdc_system'
            HangfireSchema = 'hangfire_collector'; HangfirePrepareSchema = $true; AutoCreateSchema = $true
            SyncCron = '*/2 * * * *'; WorkDirectory = (Join-Path $Root '.local/exports'); KeepLocalFiles = $true
            LocalFileRetentionDays = 3; TargetTableCompression = 'NONE'
        }
        LocalAdmin        = [ordered]@{ Enabled = $true; Username = 'admin'; PasswordHash = $DevAdminHash }
        Gcs               = [ordered]@{ Endpoint = $GcsUrl; Anonymous = $true; CredentialsPath = '' }
        Alerts            = [ordered]@{ PublicUrl = 'http://localhost:5080'; Smtp = [ordered]@{ Host = 'localhost'; Port = 1025; Security = 'None'; From = 'sqldatacollector@localhost' } }   # mailpit: http://localhost:8025
        SqlCredentialOverrides = $overrides
        Logging           = [ordered]@{ LogLevel = [ordered]@{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning'; Hangfire = 'Information' } }
    }
    $settings | ConvertTo-Json -Depth 5 | Set-Content -Path $LocalSettings -Encoding UTF8
    Write-Host ''
    Write-Host 'Ready. Start the app:  .\scripts\dev.ps1 run   ->  http://localhost:5080' -ForegroundColor Green
    if ($admin) { Write-Host "Sign in with Windows ($admin has the Admin role) or local admin: admin / Admin!Dev2026" -ForegroundColor Green }
    else { Write-Host 'Sign in as local admin: admin / Admin!Dev2026' -ForegroundColor Green }
}

switch ($Command) {
    'setup'   { Start-Sources; Initialize-Local }
    'up'      { Start-Sources }
    'reseed'  { Start-Sources -Reseed }
    'reset-local' {
        $instance = Get-LocalSqlInstance
        Write-Step "Dropping $MetaDb and $DwhDb on $instance"
        Invoke-SqlScript (Get-ConnectionString $instance 'master') @"
IF DB_ID(N'$MetaDb') IS NOT NULL BEGIN ALTER DATABASE [$MetaDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$MetaDb]; END
IF DB_ID(N'$DwhDb') IS NOT NULL BEGIN ALTER DATABASE [$DwhDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DwhDb]; END
"@
        Initialize-Local
    }
    'run' {
        if (-not (Test-Path $LocalSettings)) { throw 'Run ".\scripts\dev.ps1 setup" first.' }
        & dotnet run --project $AppProject --launch-profile LocalTest
    }
    'run-job' {
        if (-not $Arg) { throw "Usage: .\scripts\dev.ps1 run-job 'extract all' | 'extract <id>' | 'export all' | 'export <id>'" }
        Invoke-RunJob ($Arg -split '\s+')
    }
    'smoke' {
        if (-not (Test-Path $LocalSettings)) { throw 'Run ".\scripts\dev.ps1 setup" first.' }
        Start-Sources
        Write-Step 'Smoke 1/4: all extract jobs'; Invoke-RunJob @('extract', 'all')
        Write-Step 'Smoke 2/4: simulate new source data'; & $PSCommandPath simulate -Orders 50
        Write-Step 'Smoke 3/4: extract jobs again (incremental jobs must pick up only the new rows)'; Invoke-RunJob @('extract', 'all')
        Write-Step 'Smoke 4/4: all export jobs'; Invoke-RunJob @('export', 'all')
        $list = Invoke-RestMethod "$GcsUrl/storage/v1/b/test-bucket/o?prefix=exports/"
        if ($list -is [string]) { $list = $list | ConvertFrom-Json }
        $count = @($list.items).Count
        if ($count -eq 0) { throw 'SMOKE FAILED: no exported objects in the GCS emulator' }
        Write-Host "SMOKE PASSED ($count objects under gs://test-bucket/exports/)" -ForegroundColor Green
    }
    'simulate' {
        Write-Step "ERP: $Orders new orders + updates; CRM: $($Orders * 2) new activities"
        Invoke-Docker @('exec', 'sqlcollector-src-erp-1', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', $SaPassword, '-C', '-d', 'ERP',
            '-Q', "EXEC dbo.usp_SimulateActivity @NewOrders = $Orders, @Updates = $([int]($Orders / 2))")
        Invoke-Docker @('exec', 'sqlcollector-src-crm-1', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', $SaPassword, '-C', '-d', 'CRM',
            '-Q', "EXEC sales.usp_SimulateActivity @NewActivities = $($Orders * 2)")
    }
    'gcs-ls' {
        $prefix = if ($Arg) { $Arg } else { '' }
        $result = Invoke-RestMethod "$GcsUrl/storage/v1/b/test-bucket/o?prefix=$([uri]::EscapeDataString($prefix))"
        if ($result -is [string]) { $result = $result | ConvertFrom-Json }   # emulator may not send a JSON content type
        Write-Host "$(@($result.items).Count) object(s) in gs://test-bucket/$prefix"
        foreach ($o in @($result.items) | Sort-Object name) {
            '{0,14:N0}  {1,-26}  {2}' -f [long]$o.size, $o.contentType, $o.name
        }
    }
    'gcs-get' {
        if (-not $Arg) { throw 'Usage: .\scripts\dev.ps1 gcs-get <object name>' }
        $out = Join-Path $Root ('.local/gcs/' + $Arg)
        New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
        Invoke-WebRequest "$GcsUrl/storage/v1/b/test-bucket/o/$([uri]::EscapeDataString($Arg))?alt=media" -OutFile $out
        Write-Host $out
    }
    'test'             { & dotnet test (Join-Path $Root 'SqlCollector.sln') }
    'test-unit'        { & dotnet test (Join-Path $Root 'SqlCollector.sln') --filter 'Category!=Integration' }
    'test-integration' { & dotnet test (Join-Path $Root 'SqlCollector.sln') --filter 'Category=Integration' }
    'full-up' {
        Invoke-Compose @('--profile', 'full', 'up', '-d', '--build')
        Write-Host 'Embedded mode: http://localhost:8080 (admin / Admin!Dev2026); GCS emulator: http://localhost:4444'
    }
    'shared-up' {
        Invoke-Compose @('--profile', 'shared', 'up', '-d', '--build')
        Write-Host 'Shared Hangfire mode: http://localhost:8080 (admin / Admin!Dev2026); GCS emulator: http://localhost:4444'
    }
    'down' {
        $a = @('--profile', 'full', '--profile', 'shared', 'down')
        if ($Volumes) { $a += '-v' }
        Invoke-Compose $a
    }
    'status' { Invoke-Compose @('--profile', 'full', '--profile', 'shared', 'ps', '-a') }
    'logs' {
        $a = @('--profile', 'full', '--profile', 'shared', 'logs', '--tail', '200')
        if ($Arg) { $a += $Arg }
        Invoke-Compose $a
    }
    default { Get-Help $PSCommandPath -Detailed }
}
