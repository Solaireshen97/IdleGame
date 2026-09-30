#Requires -Version 7.2
[CmdletBinding()]
param(
    [switch]$Browser,
    [string]$PublishedRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This release smoke script currently targets Windows hosts.' }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $repositoryRoot 'artifacts\server-release-smoke'
$runRoot = Join-Path $artifactRoot ([Guid]::NewGuid().ToString('N'))
if ($PublishedRun) {
    if (-not [IO.Path]::IsPathFullyQualified($PublishedRun)) { throw '-PublishedRun must be an absolute smoke artifact path.' }
    $runRoot = [IO.Path]::GetFullPath($PublishedRun).TrimEnd('\')
    $artifactBoundary = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
    if (-not $runRoot.StartsWith($artifactBoundary, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($runRoot) -ne [IO.Path]::GetFullPath($artifactRoot) -or
        [IO.Path]::GetFileName($runRoot) -notmatch '^[a-f0-9]{32}$' -or
        -not (Test-Path -LiteralPath $runRoot -PathType Container)) {
        throw '-PublishedRun must name an existing random directory directly inside artifacts/server-release-smoke.'
    }
}
$invocationId = [Guid]::NewGuid().ToString('N')
$serverDirectory = Join-Path $runRoot 'server'
$clientDirectory = Join-Path $runRoot 'client'
$dataDirectory = Join-Path $runRoot 'data'
$serverProcess = $null
$http = $null

function Assert-WithinRun {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($runRoot).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Smoke output path is outside its isolated artifact directory.'
    }
    for ($entry = $resolved; $entry; $entry = [IO.Path]::GetDirectoryName($entry)) {
        if ((Test-Path -LiteralPath $entry) -and ((Get-Item -LiteralPath $entry -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Smoke output paths must not pass through symbolic links or junctions.'
        }
    }
    return $resolved
}

function Invoke-CheckedDotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
}

function Request {
    param([string]$Path, [string]$Method = 'GET', [object]$Body = $null, [string]$Token = '')
    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$baseUrl$Path")
    $response = $null
    try {
        if ($Token) { $message.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token) }
        if ($null -ne $Body) {
            $message.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 20 -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $http.SendAsync($message).GetAwaiter().GetResult()
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            ContentType = [string]$response.Content.Headers.ContentType
            Bytes = $bytes
        }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $message.Dispose()
    }
}

function Request-Json {
    param([string]$Path, [string]$Method = 'GET', [object]$Body = $null, [string]$Token = '', [int]$ExpectedStatus = 200)
    $reply = Request -Path $Path -Method $Method -Body $Body -Token $Token
    if ($reply.Status -ne $ExpectedStatus) { throw "HTTP smoke failed: $Method $Path returned $($reply.Status), expected $ExpectedStatus." }
    if ($ExpectedStatus -eq 204 -or $reply.Bytes.Length -eq 0) { return $null }
    if ($reply.ContentType -notmatch '^application/(json|problem\+json)') { throw "Expected JSON from $Path; received $($reply.ContentType)." }
    return [Text.Encoding]::UTF8.GetString($reply.Bytes) | ConvertFrom-Json
}

function Assert-StaticFile {
    param([string]$RelativePath)
    $local = Assert-WithinRun (Join-Path (Join-Path $serverDirectory 'wwwroot') $RelativePath)
    if (-not (Test-Path -LiteralPath $local -PathType Leaf)) { throw "Published client file is missing: $RelativePath" }
    $reply = Request -Path ('/' + $RelativePath.Replace('\', '/'))
    if ($reply.Status -ne 200) { throw "Static file $RelativePath returned $($reply.Status)." }
    $expected = [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($local))
    $actual = [Security.Cryptography.SHA256]::HashData($reply.Bytes)
    if ([Convert]::ToHexString($expected) -ne [Convert]::ToHexString($actual)) {
        throw "Static response differs from the published file: $RelativePath (possibly SPA fallback)."
    }
}

Push-Location $repositoryRoot
try {
    foreach ($directory in @($serverDirectory, $clientDirectory, $dataDirectory)) {
        $null = New-Item -ItemType Directory -Path (Assert-WithinRun $directory) -Force
    }
    if (-not $PublishedRun) {
        Write-Host 'Publishing isolated Release server and client...'
        Invoke-CheckedDotnet -Arguments @('publish', 'Game.Server/Game.Server.csproj', '--configuration', 'Release', '--artifacts-path', (Join-Path $runRoot 'build-server'), '--output', $serverDirectory, '--nologo')
        Invoke-CheckedDotnet -Arguments @('publish', 'Game.Client/Game.Client.csproj', '--configuration', 'Release', '--artifacts-path', (Join-Path $runRoot 'build-client'), '--output', $clientDirectory, '--nologo')
    }
    else { Write-Host "Checking existing isolated publish (no rebuild): $runRoot" }
    $clientWebRoot = Join-Path $clientDirectory 'wwwroot'
    if (-not (Test-Path -LiteralPath (Join-Path $clientWebRoot 'index.html'))) { throw 'Client publish did not produce wwwroot/index.html.' }
    $destinationWebRoot = Assert-WithinRun (Join-Path $serverDirectory 'wwwroot')
    $null = New-Item -ItemType Directory -Path $destinationWebRoot -Force
    Get-ChildItem -LiteralPath $clientWebRoot -Force | Copy-Item -Destination $destinationWebRoot -Recurse -Force

    # A random loopback port and a new database directory are always used.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $database = Assert-WithinRun (Join-Path $dataDirectory "smoke-$invocationId.db")
    if (Test-Path -LiteralPath $database) { throw 'Smoke database already exists; refusing reuse.' }
    @{ ApiBaseUrl = "$baseUrl/" } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destinationWebRoot 'appsettings.json') -Encoding utf8NoBOM
    $serverDll = Assert-WithinRun (Join-Path $serverDirectory 'Game.Server.dll')
    $stdout = Assert-WithinRun (Join-Path $runRoot "server-$invocationId.stdout.log")
    $stderr = Assert-WithinRun (Join-Path $runRoot "server-$invocationId.stderr.log")
    $startupEnvironment = @{
        ASPNETCORE_ENVIRONMENT = 'Production'
        ASPNETCORE_URLS = $baseUrl
        ConnectionStrings__GameDb = "Data Source=$database"
    }
    $savedEnvironment = @{}
    try {
        foreach ($key in $startupEnvironment.Keys) {
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $startupEnvironment[$key], 'Process')
        }
        $arguments = @(('"' + $serverDll + '"'), '--urls', $baseUrl, '--ConnectionStrings:GameDb', ('"Data Source=' + $database + '"'))
        $serverProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WorkingDirectory $serverDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    }
    finally {
        foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    }

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(5)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $initialHealth = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($serverProcess.HasExited) { throw "Smoke server exited early ($($serverProcess.ExitCode)); inspect logs in $runRoot." }
        try {
            $health = Request-Json -Path '/health'
            if ($health.status -eq 'Healthy' -and $health.database -eq 'Healthy') { $initialHealth = $health; break }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $initialHealth) { throw "Server readiness timed out; inspect logs in $runRoot." }
    $null = Request-Json -Path '/liveness'
    $null = Request-Json -Path '/readiness'
    Assert-StaticFile 'index.html'
    Assert-StaticFile 'css/app.css'
    Assert-StaticFile 'js/battle-combat.js'
    Assert-StaticFile 'Game.Client.styles.css'
    Assert-StaticFile '_framework/blazor.webassembly.js'
    $dotnetJs = Get-ChildItem -LiteralPath (Join-Path $destinationWebRoot '_framework') -Filter 'dotnet*.js' -File | Sort-Object Name | Select-Object -First 1
    if ($null -eq $dotnetJs) { throw 'Published client has no .NET JavaScript runtime asset.' }
    Assert-StaticFile ('_framework/' + $dotnetJs.Name)
    Assert-StaticFile 'appsettings.json'
    $wasm = Get-ChildItem -LiteralPath (Join-Path $destinationWebRoot '_framework') -Filter '*.wasm' -File | Select-Object -First 1
    if ($null -eq $wasm) { throw 'Published client has no WebAssembly runtime assets.' }
    Assert-StaticFile ('_framework/' + $wasm.Name)

    $name = 'smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $credentials = @{ userName = $name; password = 'Smoke!' + [Guid]::NewGuid().ToString('N') }
    $registered = Request-Json -Path '/api/user/register' -Method POST -Body $credentials
    $login = Request-Json -Path '/api/user/login' -Method POST -Body $credentials
    if (-not $login.token -or $registered.userId -ne $login.userId) { throw 'Registration/login identity mismatch.' }
    $token = $login.token
    $null = Request-Json -Path '/api/user/me' -ExpectedStatus 401
    $me = Request-Json -Path '/api/user/me' -Token $token
    if ($me.userId -ne $login.userId) { throw 'Authenticated account query returned a different identity.' }
    $character = Request-Json -Path '/api/user/characters' -Method POST -Token $token -Body @{ name = 'Smoke fighter'; professionCode = 'swordsman' }
    if ($character.characterId -le 0) { throw 'Character creation did not return an identity.' }
    $dungeons = Request-Json -Path '/api/dungeons' -Token $token
    $dungeon = $dungeons | Where-Object { $_.canEnter } | Sort-Object minimumLevel, dungeonId | Select-Object -First 1
    if ($null -eq $dungeon) { throw 'New character has no accessible dungeon.' }
    $room = Request-Json -Path '/api/rooms' -Method POST -Token $token -Body @{ dungeonId = $dungeon.dungeonId; isPreparationTimeoutEnabled = $false; isRepeatBattle = $false; isPublic = $false }
    if ($room.roomId -le 0) { throw 'Room creation did not return an identity.' }
    $null = Request-Json -Path '/api/battle/prepare' -Method POST -Token $token -Body @{ roomId = $room.roomId; expectedRoundNumber = $room.roundNumber; expectedRunSequence = $room.runSequence }
    $advanced = Request-Json -Path "/api/rooms/$($room.roomId)" -Token $token
    if ($advanced.roundNumber -le $room.roundNumber -or $advanced.monsterHp -ge $room.monsterHp) {
        throw 'Preparing the one-character room did not advance a battle round and damage the monster.'
    }
    $null = Request-Json -Path '/api/user/logout' -Method POST -Token $token -ExpectedStatus 204
    $null = Request-Json -Path '/api/user/me' -Token $token -ExpectedStatus 401

    # Check that both background scanners still complete after exercising HTTP writes.
    $workerDeadline = [DateTime]::UtcNow.AddSeconds(10)
    $backgroundAdvanced = $false
    while ([DateTime]::UtcNow -lt $workerDeadline) {
        $lastHealth = Request-Json -Path '/health'
        $backgroundAdvanced = $lastHealth.status -eq 'Healthy'
        foreach ($worker in @($initialHealth.backgroundTasks)) {
            $latest = @($lastHealth.backgroundTasks | Where-Object { $_.name -eq $worker.name })
            if ($latest.Count -ne 1 -or $latest[0].lastSuccessfulScanAtUtc -le $worker.lastSuccessfulScanAtUtc) { $backgroundAdvanced = $false }
        }
        if ($backgroundAdvanced) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $backgroundAdvanced) { throw 'Background scanners did not report a new healthy completion after the HTTP smoke.' }
    if (-not (Test-Path -LiteralPath $database)) { throw 'The isolated smoke database was not created.' }
    if ($Browser) {
        & node (Join-Path $PSScriptRoot 'verify-published-client.cjs') $baseUrl $runRoot
        if ($LASTEXITCODE -ne 0) { throw "Published Blazor browser startup failed with exit code $LASTEXITCODE." }
    }
    Write-Host "Release smoke passed: client assets, health, registration/login/logout, character/room creation, battle round, and background scanners. Artifacts: $runRoot"
}
finally {
    if ($null -ne $http) { $http.Dispose() }
    if ($null -ne $serverProcess) {
        if (-not $serverProcess.HasExited) {
            $serverProcess.Kill($true)
            if (-not $serverProcess.WaitForExit(10000)) { throw 'The smoke server process did not exit after termination.' }
        }
        $serverProcess.Dispose()
    }
    # Retain this run's logs and temporary database for diagnosis. No existing save is deleted.
    Pop-Location
}
