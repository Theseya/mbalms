# Starts the isolated test Postgres with a named volume and ensures the app database exists.
# Does not print secrets. Does not touch the live compose project `mbalms`.
# Re-runs reuse a healthy container and volume; they never delete the volume.
# Usage (repo root):  .\scripts\up-test-db.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envFile = Join-Path $root '.env.test'
if (-not (Test-Path $envFile)) {
    throw "Create .env.test from .env.test.example and set TEST_POSTGRES_PASSWORD (file not committed)."
}

function Read-DotEnv([string]$path) {
    $map = @{}
    foreach ($line in Get-Content -LiteralPath $path) {
        $trim = $line.Trim()
        if ($trim -eq '' -or $trim.StartsWith('#')) { continue }
        $eq = $trim.IndexOf('=')
        if ($eq -lt 1) { continue }
        $map[$trim.Substring(0, $eq).Trim()] = $trim.Substring($eq + 1)
    }
    return $map
}

function Assert-LastExit([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

function Test-DockerName([string]$name) {
    docker inspect $name 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}

function Get-ContainerInspect([string]$name) {
    $raw = docker inspect $name
    Assert-LastExit "docker inspect $name"
    return ($raw | ConvertFrom-Json)[0]
}

function Assert-TestPostgresLayout($info, [string]$volume, [string]$expectedPort) {
    $pgdata = '/var/lib/postgresql/data'
    foreach ($e in @($info.Config.Env)) {
        if ([string]$e -like 'PGDATA=*') {
            $pgdata = ([string]$e).Substring(7)
            break
        }
    }
    $pgdataNorm = $pgdata.TrimEnd('/')

    if ($info.HostConfig.Tmpfs) {
        foreach ($p in @($info.HostConfig.Tmpfs.PSObject.Properties.Name)) {
            if (([string]$p).TrimEnd('/') -eq $pgdataNorm) {
                throw "Refusing to reuse container: PGDATA $pgdata is tmpfs. Will not delete or recreate. Fix the container manually."
            }
        }
    }

    $dataMount = $null
    foreach ($m in @($info.Mounts)) {
        if (([string]$m.Destination).TrimEnd('/') -eq $pgdataNorm) {
            $dataMount = $m
            break
        }
    }
    if (-not $dataMount) {
        throw "Refusing to reuse container: PGDATA $pgdata has no mount. Expected named volume $volume. Will not delete or recreate."
    }
    if ([string]$dataMount.Type -eq 'tmpfs') {
        throw "Refusing to reuse container: PGDATA $pgdata is a tmpfs mount. Will not delete or recreate."
    }
    if ([string]$dataMount.Type -ne 'volume' -or [string]$dataMount.Name -ne $volume) {
        throw "Refusing to reuse container: PGDATA $pgdata is '$($dataMount.Type) $($dataMount.Name)', expected named volume $volume. Will not delete or recreate."
    }

    $bindings = $info.HostConfig.PortBindings
    $prop = if ($bindings) { $bindings.PSObject.Properties['5432/tcp'] } else { $null }
    $bindList = if ($prop) { @($prop.Value) } else { @() }
    if ($bindList.Count -eq 0) {
        throw "Refusing to reuse container: 5432/tcp is not published. Expected 127.0.0.1:$expectedPort. Will not delete or recreate."
    }
    $bind = $bindList[0]
    $hostIp = [string]$bind.HostIp
    $hostPort = [string]$bind.HostPort
    if ($hostIp -ne '127.0.0.1') {
        throw "Refusing to reuse container: 5432/tcp HostIp='$hostIp', expected 127.0.0.1. Will not delete or recreate."
    }
    if ($hostPort -ne $expectedPort) {
        throw "Refusing to reuse container: 5432/tcp published as $hostPort, expected $expectedPort. Will not delete or recreate."
    }
}

$cfg = Read-DotEnv $envFile
$user = if ($cfg.TEST_POSTGRES_USER) { $cfg.TEST_POSTGRES_USER } else { 'check' }
$bootDb = if ($cfg.TEST_POSTGRES_DB) { $cfg.TEST_POSTGRES_DB } else { 'check' }
$appDb = if ($cfg.TEST_APP_DATABASE) { $cfg.TEST_APP_DATABASE } else { 'worksheet_manual' }
$port = if ($cfg.TEST_DB_PORT) { $cfg.TEST_DB_PORT } else { '55433' }
if ([string]::IsNullOrWhiteSpace($cfg.TEST_POSTGRES_PASSWORD)) {
    throw 'TEST_POSTGRES_PASSWORD is empty in .env.test.'
}
foreach ($n in @($user, $bootDb, $appDb)) {
    if ($n -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Unsafe identifier in .env.test: $n" }
}
if ($port -notmatch '^[0-9]+$') { throw "Unsafe TEST_DB_PORT in .env.test: $port" }

$volume = 'mbalms-worksheet-check-data'
$container = 'mbalms-worksheet-check'

Write-Host "Ensuring named volume $volume (will not delete it)."
docker volume create $volume | Out-Null
Assert-LastExit "docker volume create $volume"

if (Test-DockerName $container) {
    $info = Get-ContainerInspect $container
    Assert-TestPostgresLayout $info $volume $port
    $running = [string]$info.State.Running
    if ($running -ne 'True') {
        Write-Host "Starting existing container $container."
        docker start $container | Out-Null
        Assert-LastExit "docker start $container"
    } else {
        Write-Host "Reusing running container $container."
    }
} else {
    Write-Host "Creating container $container via compose (volume reused if present)."
    docker compose -p mbalms-test -f docker-compose.test.yml --env-file $envFile up -d
    Assert-LastExit 'docker compose up'
    $info = Get-ContainerInspect $container
    Assert-TestPostgresLayout $info $volume $port
}

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    docker exec $container pg_isready -U $user -d $bootDb 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw 'Test postgres did not become ready.' }

# Identifiers already validated; avoid psql -v :'n' which is rejected by the Windows client.
$exists = docker exec $container psql -U $user -d postgres -v ON_ERROR_STOP=1 -tA -c "SELECT 1 FROM pg_database WHERE datname = '$appDb'"
Assert-LastExit "psql SELECT for database $appDb"
if (($exists | Out-String).Trim() -ne '1') {
    # Quote the role: unquoted `check` is a reserved word in PostgreSQL.
    $create = "CREATE DATABASE `"$appDb`" OWNER `"$user`";"
    $create | docker exec -i $container psql -U $user -d postgres -v ON_ERROR_STOP=1
    Assert-LastExit "psql CREATE DATABASE $appDb"
    Write-Host "Created application database $appDb."
} else {
    Write-Host "Application database $appDb already exists."
}

Write-Host 'Test postgres is up. Point the local API at Host=127.0.0.1 Port=55433 Database=worksheet_manual.'
Write-Host 'Do not run: docker compose down -v  (that would delete the named volume).'
Write-Host 'Do not use docker compose.yml from this script; live stack is separate.'
