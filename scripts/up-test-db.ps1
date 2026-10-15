# Starts the isolated test Postgres with a named volume and ensures the app database exists.
# Does not print secrets. Does not touch the live compose project `mbalms`.
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

$cfg = Read-DotEnv $envFile
$user = if ($cfg.TEST_POSTGRES_USER) { $cfg.TEST_POSTGRES_USER } else { 'check' }
$bootDb = if ($cfg.TEST_POSTGRES_DB) { $cfg.TEST_POSTGRES_DB } else { 'check' }
$appDb = if ($cfg.TEST_APP_DATABASE) { $cfg.TEST_APP_DATABASE } else { 'worksheet_manual' }
if ([string]::IsNullOrWhiteSpace($cfg.TEST_POSTGRES_PASSWORD)) {
    throw 'TEST_POSTGRES_PASSWORD is empty in .env.test.'
}
foreach ($n in @($user, $bootDb, $appDb)) {
    if ($n -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Unsafe identifier in .env.test: $n" }
}

Write-Host 'Starting mbalms-test db (named volume mbalms-worksheet-check-data, no tmpfs).'
docker compose -p mbalms-test -f docker-compose.test.yml --env-file $envFile up -d
$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    docker exec mbalms-worksheet-check pg_isready -U $user -d $bootDb 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw 'Test postgres did not become ready.' }

$exists = docker exec mbalms-worksheet-check `
    psql -U $user -d postgres -tA -v n="$appDb" -c "SELECT 1 FROM pg_database WHERE datname = :'n'"
if ($exists.Trim() -ne '1') {
    # Quote the role: unquoted `check` is a reserved word in PostgreSQL.
    $create = "CREATE DATABASE `"$appDb`" OWNER `"$user`";"
    $create | docker exec -i mbalms-worksheet-check psql -U $user -d postgres | Out-Null
    Write-Host "Created application database $appDb."
} else {
    Write-Host "Application database $appDb already exists."
}

Write-Host 'Test postgres is up. Point the local API at Host=127.0.0.1 Port=55433 Database=worksheet_manual.'
Write-Host 'Do not run: docker compose down -v  (that would delete the named volume).'
Write-Host 'Do not use docker compose.yml from this script; live stack is separate.'
