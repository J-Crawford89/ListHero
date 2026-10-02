param([Parameter(Mandatory)][string]$ReleaseDirectory)
. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1')
Assert-FreeHostingTemplate
$null = Assert-HostingAccount

$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $hostingRoot '.artifacts/azure-hosting'))
$releaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
if (!$releaseDirectory.StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Deploy only a prepared release inside this repository.'
}
foreach ($hostName in @('Api', 'Web')) {
    if (!(Test-Path -LiteralPath (Join-Path $releaseDirectory "$hostName.zip"))) { throw "Missing $hostName package." }
}

# Keep the recoverable deployment credentials encrypted for this Windows account.
# They are reused on redeployment; regenerating the encryption certificates would invalidate old links and cookies.
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$artifactInfo = [IO.DirectoryInfo]::new($artifactDirectory)
# Read/write only the access rules; rewriting an audit ACL would require SeSecurityPrivilege.
$acl = [IO.FileSystemAclExtensions]::GetAccessControl($artifactInfo, [Security.AccessControl.AccessControlSections]::Access)
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
[IO.FileSystemAclExtensions]::SetAccessControl($artifactInfo, $acl)
$secretPath = Join-Path $artifactDirectory 'beta-secrets.dpapi'
$parameterPath = Join-Path $artifactDirectory 'deployment-parameters.json'
$entropy = [Text.Encoding]::UTF8.GetBytes('ListHero free beta deployment v1')
if (!('ListHero.Deployment.CertificateEncoding' -as [Type])) {
    # The RSA PEM export takes ReadOnlySpan<char>; call it in C# rather than through PowerShell's binder.
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace ListHero.Deployment;
public static class CertificateEncoding
{
    public static string Encode(X509Certificate2 certificate, RSA key, string password)
    {
        var pem = certificate.ExportCertificatePem() + "\n" + key.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
    }
}
'@
}

function New-HostingPassword {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return 'LH!9a' + [Convert]::ToBase64String($bytes)
}
function New-HostingCertificate([string]$Name, [string]$Password) {
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$Name", $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-10), [DateTimeOffset]::UtcNow.AddYears(3))
        try {
            return [ListHero.Deployment.CertificateEncoding]::Encode($certificate, $rsa, $Password)
        }
        finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
}

function Convert-HostingCertificate([string]$Encoded, [string]$Password) {
    $bytes = [Convert]::FromBase64String($Encoded)
    if ([Text.Encoding]::UTF8.GetString($bytes).StartsWith('-----BEGIN CERTIFICATE-----')) { return $Encoded }
    # Preserve the original key identity when upgrading the first PFX deployment to encrypted PEM.
    $certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12($bytes, $Password,
        ([Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet -bor
         [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable))
    try {
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        try {
            return [ListHero.Deployment.CertificateEncoding]::Encode($certificate, $rsa, $Password)
        } finally { $rsa.Dispose() }
    } finally { $certificate.Dispose() }
}

$groupExists = Invoke-HostingAzure -Arguments @('group', 'exists', '--name', $hostingGroup, '--subscription', $hostingSubscription) -Json
if (Test-Path -LiteralPath $secretPath) {
    $clear = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($secretPath), $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { $secrets = [Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json -AsHashtable }
    finally { [Array]::Clear($clear, 0, $clear.Length) }
    foreach ($certificateName in @('apiProtectionCertificate', 'webProtectionCertificate')) {
        $secrets[$certificateName] = Convert-HostingCertificate $secrets[$certificateName] $secrets.protectionCertificatePassword
    }
    $clear = [Text.Encoding]::UTF8.GetBytes(($secrets | ConvertTo-Json -Compress))
    try {
        [IO.File]::WriteAllBytes($secretPath, [Security.Cryptography.ProtectedData]::Protect($clear, $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser))
    } finally { [Array]::Clear($clear, 0, $clear.Length) }
} else {
    if ($groupExists) {
        $existing = @(Invoke-HostingAzure -Arguments @('resource', 'list', '--resource-group', $hostingGroup, '--subscription', $hostingSubscription) -Json)
        if ($existing.Count) { throw 'Existing resources found but the encrypted credential backup is missing. Recover it before redeploying.' }
    }
    $secretFile = Join-Path $env:APPDATA 'Microsoft/UserSecrets/ListHero-Web-c1e1d3f1-1f12-435d-b397-30b3cb4dc50d/secrets.json'
    $localSettings = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json -AsHashtable
    $webSecret = $localSettings['EntraExternalId:ClientSecret']
    if (!$webSecret) { throw 'The configured local web credential was not found. Never paste it into chat.' }
    $certificatePassword = New-HostingPassword
    $secrets = @{
        sqlAdminPassword = New-HostingPassword
        apiDatabasePassword = New-HostingPassword
        cacheDatabasePassword = New-HostingPassword
        webClientSecret = $webSecret
        protectionCertificatePassword = $certificatePassword
        apiProtectionCertificate = New-HostingCertificate 'ListHero.Api beta keys' $certificatePassword
        webProtectionCertificate = New-HostingCertificate 'ListHero.Web beta keys' $certificatePassword
    }
    $clear = [Text.Encoding]::UTF8.GetBytes(($secrets | ConvertTo-Json -Compress))
    try {
        [IO.File]::WriteAllBytes($secretPath, [Security.Cryptography.ProtectedData]::Protect($clear, $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser))
    } finally { [Array]::Clear($clear, 0, $clear.Length) }
}

$parameters = @{}
foreach ($name in $secrets.Keys) { $parameters[$name] = @{ value = $secrets[$name] } }
@{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#';
    contentVersion = '1.0.0.0'; parameters = $parameters } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $parameterPath -Encoding UTF8
$temporaryFirewall = $null
$environmentNames = @('LISTHERO_DEPLOY_ADMIN_CONNECTION', 'LISTHERO_DEPLOY_API_PASSWORD', 'LISTHERO_DEPLOY_CACHE_PASSWORD')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    foreach ($provider in @('Microsoft.Web', 'Microsoft.Sql')) {
        $registration = Invoke-HostingAzure -Arguments @('provider', 'show', '--namespace', $provider,
            '--subscription', $hostingSubscription, '--query', 'registrationState') -Json
        if ($registration -ne 'Registered') {
            Invoke-HostingAzure -Arguments @('provider', 'register', '--namespace', $provider, '--wait', '--subscription', $hostingSubscription)
        }
    }
    if (!$groupExists) { Invoke-HostingAzure -Arguments @('group', 'create', '--name', $hostingGroup, '--location', 'centralus', '--subscription', $hostingSubscription) }
    $deployment = @('--resource-group', $hostingGroup, '--name', 'listhero-free-beta', '--subscription', $hostingSubscription,
        '--template-file', (Join-Path $hostingRoot 'infra/azure/free-beta.json'), '--parameters', "@$parameterPath")
    Invoke-HostingAzure -Arguments (@('deployment', 'group', 'validate') + $deployment)
    Write-Host 'Azure validation accepted the free template. Creating the free beta resources.'
    Invoke-HostingAzure -Arguments (@('deployment', 'group', 'create') + $deployment)
    $outputs = Invoke-HostingAzure -Arguments @('deployment', 'group', 'show', '--resource-group', $hostingGroup,
        '--name', 'listhero-free-beta', '--subscription', $hostingSubscription, '--query', 'properties.outputs') -Json
    $webName = $outputs.webName.value; $apiName = $outputs.apiName.value; $sqlName = $outputs.sqlName.value
    $db = Invoke-HostingAzure -Arguments @('sql', 'db', 'show', '--resource-group', $hostingGroup, '--server', $sqlName,
        '--name', 'ListHeroBeta', '--subscription', $hostingSubscription,
        '--query', '{free:useFreeLimit,behavior:freeLimitExhaustionBehavior}') -Json
    if ($db.free -ne $true -or $db.behavior -ne 'AutoPause') { throw 'The database free/pause settings did not match. Deployment stopped.' }
    $plan = Invoke-HostingAzure -Arguments @('appservice', 'plan', 'show', '--resource-group', $hostingGroup,
        '--name', 'asp-listhero-beta', '--subscription', $hostingSubscription, '--query', 'sku') -Json
    if ($plan.name -ne 'F1') { throw 'The hosting plan is not F1. Deployment stopped.' }

    $addresses = [Collections.Generic.HashSet[string]]::new()
    foreach ($appName in @($apiName, $webName)) {
        $app = Invoke-HostingAzure -Arguments @('webapp', 'show', '--resource-group', $hostingGroup, '--name', $appName,
            '--subscription', $hostingSubscription, '--query', '{outbound:outboundIpAddresses}') -Json
        foreach ($address in $app.outbound.Split(',')) { if ($address) { $null = $addresses.Add($address) } }
    }
    $index = 0
    foreach ($address in $addresses) {
        Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'create', '--resource-group', $hostingGroup,
            '--server', $sqlName, '--name', "ListHeroApps-$index", '--start-ip-address', $address,
            '--end-ip-address', $address, '--subscription', $hostingSubscription)
        $index++
    }
    $localAddress = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
    if ($localAddress -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw 'Could not obtain an IPv4 address for temporary migration access.' }
    $temporaryFirewall = 'ListHeroBootstrap-' + [guid]::NewGuid().ToString('N')
    Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'create', '--resource-group', $hostingGroup,
        '--server', $sqlName, '--name', $temporaryFirewall, '--start-ip-address', $localAddress,
        '--end-ip-address', $localAddress, '--subscription', $hostingSubscription)
    $env:LISTHERO_DEPLOY_ADMIN_CONNECTION = "Server=tcp:$sqlName.database.windows.net,1433;Database=ListHeroBeta;User ID=ListHeroBootstrap;Password=$($secrets.sqlAdminPassword);Encrypt=True;TrustServerCertificate=False;Pooling=False;Connect Timeout=60;ConnectRetryCount=3;ConnectRetryInterval=10;"
    $env:LISTHERO_DEPLOY_API_PASSWORD = $secrets.apiDatabasePassword
    $env:LISTHERO_DEPLOY_CACHE_PASSWORD = $secrets.cacheDatabasePassword
    & dotnet (Join-Path $PSScriptRoot 'HostingDatabase/bin/Release/net10.0/HostingDatabase.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Database bootstrap failed; applications were not deployed.' }
    foreach ($hostName in @('Api', 'Web')) {
        $appName = if ($hostName -eq 'Api') { $apiName } else { $webName }
        Write-Host "Deploying List Hero $hostName."
        Invoke-HostingAzure -Arguments @('webapp', 'deploy', '--resource-group', $hostingGroup, '--name', $appName,
            '--src-path', (Join-Path $releaseDirectory "$hostName.zip"), '--type', 'zip', '--subscription', $hostingSubscription)
    }
    $outputs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'deployment-state.json') -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Test-FreeBeta.ps1')
    Write-Host "Web: $($outputs.webUrl.value)"
    Write-Host 'Add the hosted sign-in/sign-out callback URLs to the existing External ID web registration before testing sign-in.'
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if (Test-Path -LiteralPath $parameterPath) { Remove-Item -LiteralPath $parameterPath -Force }
    if ($temporaryFirewall) {
        Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'delete', '--resource-group', $hostingGroup,
            '--server', $sqlName, '--name', $temporaryFirewall, '--subscription', $hostingSubscription)
    }
}
