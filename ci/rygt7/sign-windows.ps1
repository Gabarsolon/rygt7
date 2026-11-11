# Authenticode-signs Windows binaries with a self-signed code-signing certificate.
#
# If CODESIGN_PFX_BASE64 (base64 PFX) and CODESIGN_PFX_PASSWORD are set, that
# certificate is used, so every CI build carries the same signer and users can
# trust it once. Otherwise a throwaway self-signed certificate is generated for
# this run only (the binary is signed, but the signer changes every build).
# Signing never requires or modifies a certificate store.
param([Parameter(Mandatory=$true)][string[]]$Path)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signtool = Get-ChildItem -LiteralPath $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\10\.\d+\.\d+\.\d+\\x64\\signtool\.exe$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (!$signtool) { throw 'signtool.exe (Windows 10 SDK, x64) not found' }

$pfx = Join-Path ([IO.Path]::GetTempPath()) ("rygt-codesign-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    if ($env:CODESIGN_PFX_BASE64) {
        if (!$env:CODESIGN_PFX_PASSWORD) { throw 'CODESIGN_PFX_PASSWORD is required with CODESIGN_PFX_BASE64' }
        $pfxBytes = [Convert]::FromBase64String($env:CODESIGN_PFX_BASE64.Trim())
        [IO.File]::WriteAllBytes($pfx, $pfxBytes)
        # Secrets set by piping (or the interactive prompt) can pick up a trailing newline.
        $password = $env:CODESIGN_PFX_PASSWORD.Trim()
        $pfxHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($pfxBytes)).Replace('-', '').Substring(0, 12)
        Write-Output ("PFX secret: {0} bytes, sha256 {1}; password: {2} chars ({3} before trimming)" -f
            $pfxBytes.Length, $pfxHash, $password.Length, $env:CODESIGN_PFX_PASSWORD.Length)
        $origin = 'configured self-signed certificate'
    } else {
        $password = [guid]::NewGuid().ToString('N')
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=RyGT-Team, O=RyGT-Team', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $usage = [Security.Cryptography.OidCollection]::new()
        [void]$usage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))  # Code Signing
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $false))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $now = [DateTimeOffset]::UtcNow
        $generated = $request.CreateSelfSigned($now.AddDays(-1), $now.AddDays(30))
        [IO.File]::WriteAllBytes($pfx, $generated.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
        $origin = 'ephemeral self-signed certificate'
    }
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$password" }
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $password)
    Write-Output "Signing with $origin $($cert.Subject) [$($cert.Thumbprint)]"

    foreach ($file in $Path) {
        $target = (Resolve-Path -LiteralPath $file).Path
        $base = @('sign', '/fd', 'SHA256', '/f', $pfx, '/p', $password, '/d', 'RyGT')
        & $signtool.FullName @base '/tr' 'http://timestamp.digicert.com' '/td' 'SHA256' $target
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Timestamp server unavailable; signing $target without a timestamp"
            & $signtool.FullName @base $target
            if ($LASTEXITCODE -ne 0) { throw "signtool failed for $target" }
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $target
        if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
            throw "Signature verification failed for $target ($($signature.Status))"
        }
        Write-Output "Signed $target"
    }
} finally {
    Remove-Item -LiteralPath $pfx -Force -ErrorAction SilentlyContinue
}
