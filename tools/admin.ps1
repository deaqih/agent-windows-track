param(
    [ValidateSet('devices', 'enrollment-token', 'export')][string]$Action = 'devices',
    [uri]$ServerUrl = 'https://boneiset.awancode.com',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($ServerUrl.Scheme -ne 'https' -and -not ($ServerUrl.Scheme -eq 'http' -and $ServerUrl.IsLoopback)) { throw 'HTTPS wajib selain untuk localhost.' }
if ($ServerUrl.UserInfo -or $ServerUrl.Query -or $ServerUrl.Fragment) { throw 'Alamat server tidak boleh mengandung kredensial, query, atau fragment.' }
$key = $env:BONEISET_ADMIN_KEY
if (-not $key) {
    $secure = Read-Host 'Admin API key' -AsSecureString
    $key = [System.Net.NetworkCredential]::new('', $secure).Password
}
if ($key -notmatch '^[0-9a-fA-F]{64}$') { throw 'Admin API key tidak valid.' }
$base = $ServerUrl.AbsoluteUri.TrimEnd('/')
$headers = @{ Authorization = "Bearer $key" }
switch ($Action) {
    'devices' { Invoke-RestMethod -Uri "$base/api/v1/admin/devices" -Headers $headers }
    'enrollment-token' { Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/enrollment-tokens" -Headers $headers }
    'export' {
        if (-not $OutputPath) { throw 'Tentukan -OutputPath untuk file CSV.' }
        if (Test-Path -LiteralPath $OutputPath) { throw 'File tujuan sudah ada; pilih nama lain.' }
        Invoke-WebRequest -Uri "$base/api/v1/admin/export/assets.csv" -Headers $headers -OutFile $OutputPath
        Write-Output "Ekspor tersimpan: $OutputPath"
    }
}
