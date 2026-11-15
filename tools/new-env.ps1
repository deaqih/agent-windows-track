$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$template = Get-Content -LiteralPath (Join-Path $root '.env.example') -Raw
$key = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$content = $template.Replace('ADMIN_API_KEY=', "ADMIN_API_KEY=$key")
$path = Join-Path $root '.env'
$stream = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew)
$stream.Dispose()
if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    $acl = Get-Acl -LiteralPath $path
    $acl.SetAccessRuleProtection($true, $false)
    $current = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($identity in @($current, [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
} else {
    [System.IO.File]::SetUnixFileMode($path, [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
}
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
Write-Output 'File .env dibuat dengan akses terbatas. Isi koneksi PostgreSQL, nama network, dan IP proxy sesuai README. Jangan commit file ini.'
