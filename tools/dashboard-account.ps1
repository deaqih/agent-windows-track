param([Parameter(Mandatory)][string]$Username, [switch]$ResetPassword, [switch]$Docker)
$ErrorActionPreference = 'Stop'
if ($Username -notmatch '^[a-zA-Z0-9@._-]{3,100}$') { throw 'Nama pengguna tidak valid.' }
$secure = Read-Host 'Kata sandi baru (14-128 karakter)' -AsSecureString
$confirmation = Read-Host 'Ulangi kata sandi baru' -AsSecureString
$password = [System.Net.NetworkCredential]::new('', $secure).Password
$repeat = [System.Net.NetworkCredential]::new('', $confirmation).Password
if ($password.Length -lt 14 -or $password.Length -gt 128 -or [string]::IsNullOrWhiteSpace($password) -or $password -cne $repeat) {
    throw 'Kata sandi harus 14-128 karakter dan kedua isian harus sama.'
}
$previous = $env:BONEISET_ADMIN_PASSWORD
$env:BONEISET_ADMIN_PASSWORD = $password
$root = Split-Path $PSScriptRoot -Parent
$command = if ($ResetPassword) { '--reset-password' } else { '--create-admin' }
Push-Location $root
try {
    if ($Docker) {
        & docker compose exec -T -e BONEISET_ADMIN_PASSWORD server dotnet Boneiset.Server.dll $command $Username
    } else {
        $runtime = Join-Path $root '.tools\dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $runtime)) { $runtime = 'dotnet' }
        & $runtime run --project src/Boneiset.Server -c Release --no-build -- $command $Username
    }
    if ($LASTEXITCODE -ne 0) { throw 'Perubahan akun gagal. Periksa koneksi database dan konfigurasi server.' }
} finally {
    Pop-Location
    $env:BONEISET_ADMIN_PASSWORD = $previous
    $password = $repeat = $null
    $secure.Dispose()
    $confirmation.Dispose()
}
