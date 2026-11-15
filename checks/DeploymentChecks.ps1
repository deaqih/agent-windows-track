param([string]$ComposeExecutable)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path $root '.local'))
$temporary = Join-Path $temporaryRoot ('deployment-check-' + [Guid]::NewGuid().ToString('N'))
$previousEnvironment = @{}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    $null = New-Item -ItemType Directory -Path (Join-Path $temporary 'tools') -Force
    Copy-Item -LiteralPath (Join-Path $root '.env.example') -Destination $temporary
    Copy-Item -LiteralPath (Join-Path $root 'tools/new-env.ps1') -Destination (Join-Path $temporary 'tools')
    $script = Join-Path $temporary 'tools/new-env.ps1'
    $envFile = Join-Path $temporary '.env'
    & $script | Out-Null
    $content = Get-Content -LiteralPath $envFile -Raw
    $values = ConvertFrom-StringData $content
    Assert-Check ($values.ADMIN_API_KEY -cmatch '^[0-9A-F]{64}$') 'Expected a random 256-bit API key.'
    foreach ($name in @('DATABASE_CONNECTION_STRING', 'POSTGRES_NETWORK', 'TRUSTED_PROXY', 'NGINX_NETWORK')) {
        Assert-Check ($values.ContainsKey($name) -and $values[$name] -eq '') "Expected operator-supplied $name."
    }
    Assert-Check ($values.BONEISET_HTTP_PORT -eq '5087') 'Expected loopback port default.'
    Assert-Check ($values.Count -eq 6) 'Unexpected environment setting.'
    $rejected = $false
    try { & $script | Out-Null } catch { $rejected = $true }
    Assert-Check $rejected 'Existing secrets must not be overwritten.'
    Assert-Check ((Get-Content -LiteralPath $envFile -Raw) -ceq $content) 'Existing environment file changed.'
    if ($IsWindows) {
        Assert-Check (Get-Acl -LiteralPath $envFile).AreAccessRulesProtected 'Expected private, non-inherited permissions.'
    } else {
        $privateMode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
        Assert-Check ([System.IO.File]::GetUnixFileMode($envFile) -eq $privateMode) 'Expected mode 0600.'
    }
    Write-Output 'PASS: 10 deployment environment checks; existing secrets preserved.'
    if ($ComposeExecutable) {
        foreach ($name in $values.Keys) {
            $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
        }
        $fixture = @'
DATABASE_CONNECTION_STRING='Host=postgres-existing;Database=boneiset_test;Username=fixture;Password=synthetic$dollar'
POSTGRES_NETWORK=existing-postgres-network
TRUSTED_PROXY=172.30.0.2
BONEISET_HTTP_PORT=5087
NGINX_NETWORK=existing-nginx-network
'@
        $fixture += "`nADMIN_API_KEY=$($values.ADMIN_API_KEY)`n"
        [System.IO.File]::WriteAllText($envFile, $fixture)
        $baseArguments = @('--project-directory', $root, '--env-file', $envFile, '-p', 'boneiset-config-check', '-f', (Join-Path $root 'compose.yaml'))
        foreach ($mode in @('host', 'docker', 'development')) {
            $arguments = $baseArguments
            if ($mode -eq 'docker') { $arguments += @('-f', (Join-Path $root 'compose.nginx.yaml')) }
            if ($mode -eq 'development') { $arguments += @('-f', (Join-Path $root 'compose.dev.yaml')) }
            $json = & $ComposeExecutable @arguments config --format json
            Assert-Check ($LASTEXITCODE -eq 0) "Compose config failed: $mode."
            $config = ($json -join "`n") | ConvertFrom-Json
            Assert-Check (@($config.services.PSObject.Properties.Name).Count -eq 1 -and $null -ne $config.services.server) 'Expected only the application container.'
            Assert-Check ($config.networks.database.external -and $config.networks.database.name -eq 'existing-postgres-network') 'Expected existing PostgreSQL network.'
            Assert-Check (@($config.volumes.PSObject.Properties.Name).Count -eq 1 -and $null -ne $config.volumes.dashboard_keys) 'Unexpected managed data volume.'
            Assert-Check ($config.services.server.environment.ConnectionStrings__Database.Replace('$$', '$').EndsWith('Password=synthetic$dollar')) 'Connection string interpolation changed the password.'
            $environment = if ($mode -eq 'development') { 'Development' } else { 'Production' }
            Assert-Check ($config.services.server.environment.ASPNETCORE_ENVIRONMENT -eq $environment) 'Unexpected application environment.'
            if ($mode -eq 'docker') {
                Assert-Check (-not $config.services.server.ports) 'Container Nginx must not publish an app host port.'
                Assert-Check ($config.networks.proxy.external -and $config.networks.proxy.name -eq 'existing-nginx-network') 'Expected existing Nginx network.'
                Assert-Check ($config.services.server.networks.proxy.aliases -contains 'boneiset-server') 'Missing proxy DNS alias.'
            } else {
                Assert-Check (@($config.services.server.ports).Count -eq 1 -and $config.services.server.ports[0].host_ip -eq '127.0.0.1') 'App port must be loopback-only.'
                Assert-Check ($config.services.server.ports[0].published -eq '5087' -and $config.services.server.ports[0].target -eq 8080) 'Unexpected app port mapping.'
            }
            Write-Output "PASS: Compose $mode configuration; no database or proxy container."
        }
        foreach ($name in @('DATABASE_CONNECTION_STRING', 'POSTGRES_NETWORK', 'TRUSTED_PROXY', 'ADMIN_API_KEY')) {
            [System.IO.File]::WriteAllText($envFile, [regex]::Replace($fixture, "(?m)^$name=.*$", "$name="))
            $null = & $ComposeExecutable @baseArguments config --quiet 2>&1
            Assert-Check ($LASTEXITCODE -ne 0) "Missing $name must fail configuration."
        }
        [System.IO.File]::WriteAllText($envFile, [regex]::Replace($fixture, '(?m)^NGINX_NETWORK=.*$', 'NGINX_NETWORK='))
        $null = & $ComposeExecutable @baseArguments config --quiet 2>&1
        Assert-Check ($LASTEXITCODE -eq 0) 'Host Nginx must not require a proxy network.'
        $null = & $ComposeExecutable @baseArguments -f (Join-Path $root 'compose.nginx.yaml') config --quiet 2>&1
        Assert-Check ($LASTEXITCODE -ne 0) 'Container Nginx must require an existing proxy network.'
        Write-Output 'PASS: Missing production settings rejected.'
    } else {
        Write-Output 'SKIP: Compose checks; pass -ComposeExecutable with a standalone Docker Compose binary.'
    }
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$name]) {
            Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
    if (Test-Path -LiteralPath $temporary) {
        $resolved = (Resolve-Path -LiteralPath $temporary).Path
        if (-not $resolved.StartsWith($temporaryRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside the test directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
