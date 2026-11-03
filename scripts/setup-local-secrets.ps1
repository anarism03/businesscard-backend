# Writes the local development secrets (connection string + JWT key) to dotnet user-secrets.
# Reads the PostgreSQL password from ..\.env (POSTGRES_PASSWORD=...), generates a fresh JWT key.
# Values are never printed.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$project = Join-Path $root 'src\BusinessCard.Api'

$envLine = Get-Content (Join-Path $root '.env') | Where-Object { $_ -like 'POSTGRES_PASSWORD=*' } | Select-Object -First 1
if (-not $envLine) { throw 'POSTGRES_PASSWORD is missing in .env' }
$dbPassword = $envLine.Substring('POSTGRES_PASSWORD='.Length).Trim()

$bytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$jwtKey = [Convert]::ToBase64String($bytes)

$connection = "Host=localhost;Port=5432;Database=businesscard_db;Username=businesscard_app;Password=$dbPassword"
dotnet user-secrets set 'ConnectionStrings:DefaultConnection' $connection --project $project | Out-Null
dotnet user-secrets set 'Jwt:Key' $jwtKey --project $project | Out-Null
'User-secrets saved: ConnectionStrings:DefaultConnection, Jwt:Key'
