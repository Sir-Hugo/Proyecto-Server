[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$passwordBytes = New-Object byte[] 36
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($passwordBytes)
$rng.Dispose()
$password = [Convert]::ToBase64String($passwordBytes)
$project = "proyecto-server-bot-$([Guid]::NewGuid().ToString('N'))"
$envFile = Join-Path ([IO.Path]::GetTempPath()) ("game-bot-{0}.env" -f [Guid]::NewGuid().ToString('N'))
$dockerConfig = Join-Path ([IO.Path]::GetTempPath()) ("game-bot-docker-{0}" -f [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $dockerConfig | Out-Null
    $previousDockerConfig = $env:DOCKER_CONFIG
    $env:DOCKER_CONFIG = $dockerConfig
    @(
        'POSTGRES_DB=game_bot'
        'POSTGRES_USER=game_bot'
        "POSTGRES_PASSWORD=$password"
        "POSTGRES_PORT=$port"
        'AUTH0_AUTHORITY=https://unused.bot.local/'
        'AUTH0_AUDIENCE=unused-bot'
        'AUTH0_CLIENT_ID=unused-bot'
    ) | Set-Content -LiteralPath $envFile -Encoding utf8

    docker compose -p $project --env-file $envFile up -d --wait postgres
    if ($LASTEXITCODE -ne 0) { throw 'The isolated PostgreSQL 17 container did not become healthy.' }

    $env:BOT_CONNECTION_STRING = "Host=127.0.0.1;Port=$port;Database=game_bot;Username=game_bot;Password=$password;SSL Mode=Disable"
    dotnet run --project (Join-Path $PSScriptRoot 'Game.Bot\Game.Bot.csproj') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "The bot exited with code $LASTEXITCODE." }
}
finally {
    Remove-Item Env:BOT_CONNECTION_STRING -ErrorAction SilentlyContinue
    $cleanupErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & docker compose -p $project --env-file $envFile down -v 2>$null | Out-Null
    $ErrorActionPreference = $cleanupErrorPreference
    $env:DOCKER_CONFIG = $previousDockerConfig

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    foreach ($path in @($envFile, $dockerConfig)) {
        $resolved = [IO.Path]::GetFullPath($path)
        if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unexpected temporary path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
