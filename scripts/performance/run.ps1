[CmdletBinding()]
param([string]$ResultsPath)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ResultsPath)) { $ResultsPath = Join-Path $PSScriptRoot 'results\phase5a-results.json' }
$project = 'proyecto-server-performance'
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$passwordBytes = New-Object byte[] 36
$rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($passwordBytes); $rng.Dispose()
$password = [Convert]::ToBase64String($passwordBytes)
$envFile = Join-Path ([IO.Path]::GetTempPath()) ("game-performance-{0}.env" -f [Guid]::NewGuid().ToString('N'))
$dockerConfig = Join-Path ([IO.Path]::GetTempPath()) ("game-performance-docker-{0}" -f [Guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $dockerConfig | Out-Null
  $previousDockerConfig = $env:DOCKER_CONFIG; $env:DOCKER_CONFIG = $dockerConfig
  @("POSTGRES_DB=game_performance", "POSTGRES_USER=game_performance", "POSTGRES_PASSWORD=$password", "POSTGRES_PORT=$port", "AUTH0_AUTHORITY=https://unused.performance.local/", "AUTH0_AUDIENCE=unused-performance", "AUTH0_CLIENT_ID=unused-performance") | Set-Content -LiteralPath $envFile -Encoding utf8
  docker compose -p $project --env-file $envFile up -d --wait postgres
  if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL 17 did not become healthy.' }
  $env:PERFORMANCE_CONNECTION_STRING = "Host=127.0.0.1;Port=$port;Database=game_performance;Username=game_performance;Password=$password;SSL Mode=Disable"
  $env:PERFORMANCE_OUTPUT = [IO.Path]::GetFullPath($ResultsPath)
  dotnet run --project (Join-Path $PSScriptRoot 'Game.Performance\Game.Performance.csproj') -c Release
  if ($LASTEXITCODE -ne 0) { throw "Performance runner failed with exit code $LASTEXITCODE." }
}
finally {
  Remove-Item Env:PERFORMANCE_CONNECTION_STRING -ErrorAction SilentlyContinue
  Remove-Item Env:PERFORMANCE_OUTPUT -ErrorAction SilentlyContinue
  $cleanupErrorPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  & docker compose -p $project --env-file $envFile down -v 2>$null | Out-Null
  $ErrorActionPreference = $cleanupErrorPreference
  $env:DOCKER_CONFIG = $previousDockerConfig
  $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()); $resolved = [IO.Path]::GetFullPath($envFile)
  if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary env path.' }
  Remove-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
  $resolvedDockerConfig = [IO.Path]::GetFullPath($dockerConfig)
  if (-not $resolvedDockerConfig.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected Docker config path.' }
  Remove-Item -LiteralPath $resolvedDockerConfig -Recurse -Force -ErrorAction SilentlyContinue
}
