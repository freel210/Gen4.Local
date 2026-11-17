[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $ConfigPath,
    [string] $ClientCertPath,
    [string] $ClientKeyPath,
    [string] $ClientCertThumbprint,
    [ValidateSet('Any', 'CurrentUser', 'LocalMachine')] [string] $ClientCertStore = 'Any',
    [string] $RootCaPath,
    [string] $MasterPassword,
    [string] $Token,
    [string] $TokenPath,
    [int] $ExpectedStatus = 200,
    [string] $ExpectProperty = 'daysBeforeNotificationOfCertificateExpiration',
    [switch] $SkipToken,
    [switch] $HideToken,
    [switch] $AllowTransportFailure
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or later is required. Run this script with pwsh.'
}

function Find-ClientCertificate {
    param([string] $Thumbprint, [string] $Location)

    $locations = switch ($Location) {
        'CurrentUser' { @([System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser) }
        'LocalMachine' { @([System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine) }
        default {
            @(
                [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
                [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
            )
        }
    }

    foreach ($storeLocation in $locations) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', $storeLocation)
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            $match = $store.Certificates | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
            if ($match) {
                return $match
            }
        }
        finally {
            $store.Close()
        }
    }

    throw "Client certificate with thumbprint '$Thumbprint' was not found in $Location store."
}

function New-ClientHandler {
    param([string] $CertPath, [string] $KeyPath, [string] $RootPath, [string] $Thumbprint, [string] $Store)

    $handler = [System.Net.Http.SocketsHttpHandler]::new()

    if ($CertPath -and $Thumbprint) {
        throw 'Use either -ClientCertPath or -ClientCertThumbprint, not both.'
    }

    if ($CertPath) {
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($CertPath, $KeyPath)
        [byte[]] $pkcs12 = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12)
        $handler.SslOptions.ClientCertificates = [System.Security.Cryptography.X509Certificates.X509CertificateCollection]::new()
        $handler.SslOptions.ClientCertificates.Add([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pkcs12)) | Out-Null
    }
    elseif ($Thumbprint) {
        $certificate = Find-ClientCertificate -Thumbprint $Thumbprint -Location $Store
        $handler.SslOptions.ClientCertificates = [System.Security.Cryptography.X509Certificates.X509CertificateCollection]::new()
        $handler.SslOptions.ClientCertificates.Add($certificate) | Out-Null
    }

    if ($RootPath) {
        $root = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content -Raw -LiteralPath $RootPath))
        $policy = [System.Security.Cryptography.X509Certificates.X509ChainPolicy]::new()
        $policy.TrustMode = [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $policy.CustomTrustStore.Add($root) | Out-Null
        $policy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $handler.SslOptions.CertificateChainPolicy = $policy
    }

    return $handler
}

function Send-Request {
    param($Client, [string] $Uri, [string] $Authorization, $Content)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
    if ($Content) {
        $request.Method = [System.Net.Http.HttpMethod]::Post
        $request.Content = $Content
    }

    if ($Authorization) {
        $request.Headers.TryAddWithoutValidation('Authorization', $Authorization) | Out-Null
    }

    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $body }
}

$base = $BaseUrl.TrimEnd('/')

if (-not $TokenPath) {
    $TokenPath = if ($ConfigPath.StartsWith('/core/')) { '/core/api/tokens/master' } else { '/api/tokens/master' }
}

$handler = New-ClientHandler -CertPath $ClientCertPath -KeyPath $ClientKeyPath -RootPath $RootCaPath -Thumbprint $ClientCertThumbprint -Store $ClientCertStore
$client = [System.Net.Http.HttpClient]::new($handler)

try {
    if (-not $SkipToken -and -not $Token) {
        if (-not $MasterPassword) {
            throw 'Either -Token or -MasterPassword is required unless -SkipToken is set.'
        }

        $payload = @{ password = $MasterPassword } | ConvertTo-Json -Compress
        $content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
        $tokenResponse = Send-Request -Client $client -Uri "$base$TokenPath" -Content $content

        if ($tokenResponse.Status -ne 200) {
            Write-Output "token request: HTTP $($tokenResponse.Status)"
            Write-Output $tokenResponse.Body
            exit 1
        }

        $tokenJson = $tokenResponse.Body | ConvertFrom-Json
        $Token = if ($tokenJson.accessToken) { $tokenJson.accessToken } else { $tokenJson.AccessToken }
        Write-Output 'token request: HTTP 200'
        if (-not $HideToken) {
            Write-Output "access token: $Token"
        }
    }

    $authorization = if ($SkipToken) { $null } elseif ($Token) { "Bearer $Token" } else { $null }
    $result = Send-Request -Client $client -Uri "$base$ConfigPath" -Authorization $authorization

    Write-Output "config request: HTTP $($result.Status)"
    Write-Output $result.Body

    if ($result.Status -ne $ExpectedStatus) {
        Write-Output "expected HTTP $ExpectedStatus"
        exit 1
    }

    if ($result.Status -eq 200 -and $ExpectProperty) {
        $json = $result.Body | ConvertFrom-Json
        if ($null -eq $json.$ExpectProperty) {
            Write-Output "expected property '$ExpectProperty' in the response body"
            exit 1
        }
    }

    exit 0
}
catch {
    $exception = $_.Exception
    $messages = @()
    while ($exception) {
        $messages += $exception.Message
        $exception = $exception.InnerException
    }

    Write-Output "transport error: $($messages -join ' / ')"

    if ($AllowTransportFailure) {
        exit 0
    }

    exit 1
}
finally {
    $client.Dispose()
}
