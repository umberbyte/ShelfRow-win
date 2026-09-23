param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0',
    [ValidateSet('x64', 'arm64')]
    [string[]]$Architecture = @('x64', 'arm64'),
    [string]$IsccPath,
    [ValidatePattern('^https://[a-z0-9]+\.codesigning\.azure\.net/?$')]
    [string]$SigningEndpoint,
    [string]$SigningAccountName,
    [string]$CertificateProfileName,
    [ValidateSet('PublicTrust', 'PrivateTrust')]
    [string]$SigningProfileType = 'PublicTrust',
    [string]$PrivateTrustRootCertificatePath,
    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'artifacts\installer'
$prerequisites = Join-Path $work 'prerequisites'
$dist = Join-Path $work 'dist'
# Use a fresh staging folder so stale binaries cannot enter a new release.
New-Item -ItemType Directory -Force $prerequisites, $dist | Out-Null
$privateTrustOutputs = @(
    (Join-Path $dist 'ArtifactSigning-PrivateTrust-Root.cer'),
    (Join-Path $dist 'ArtifactSigning-PrivateTrust-Root.cer.sha256'),
    (Join-Path $dist 'PRIVATE-TRUST-ja.txt')
)
$privateTrustOutputs | Where-Object { Test-Path -LiteralPath $_ } | Remove-Item -Force

if (-not $IsccPath) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $work 'tools\InnoSetup\{app}\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Inno Setup 6 ISCC.exe is required. Pass -IsccPath with its location.'
}

$downloads = @{
    'MicrosoftEdgeWebview2Setup.exe' = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
}
foreach ($targetArchitecture in $Architecture) {
    $downloads["vc_redist.$targetArchitecture.exe"] = "https://aka.ms/vs/17/release/vc_redist.$targetArchitecture.exe"
}
foreach ($name in $downloads.Keys) {
    $file = Join-Path $prerequisites $name
    if (-not (Test-Path -LiteralPath $file)) {
        Invoke-WebRequest $downloads[$name] -OutFile $file -UseBasicParsing
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Invalid Microsoft signature: $file"
    }
}

$signingValues = @($SigningEndpoint, $SigningAccountName, $CertificateProfileName)
$signingEnabled = ($signingValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
if ($signingEnabled -and ($signingValues | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw 'SigningEndpoint, SigningAccountName, and CertificateProfileName must all be supplied to sign artifacts.'
}

$signToolPath = $null
$dlibPath = $null
$metadataPath = $null
if ($signingEnabled) {
    $portableAzureCliBin = Join-Path $work 'tools\azure-cli-2.90.0-x64\bin'
    if (-not (Get-Command az -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $portableAzureCliBin 'az.cmd'))) {
        $env:Path = "$portableAzureCliBin;$env:Path"
    }
    $localDotNetRoot = Join-Path $work 'tools\dotnet8-x64'
    $systemX64DotNetRoot = Join-Path $env:ProgramFiles 'dotnet\x64'
    $signingDotNetRoot = @($systemX64DotNetRoot, $localDotNetRoot) |
        Where-Object { Get-ChildItem (Join-Path $_ 'shared\Microsoft.NETCore.App\8.*') -Directory -ErrorAction SilentlyContinue } |
        Select-Object -First 1
    if (-not $signingDotNetRoot) {
        $releaseIndex = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json'
        $runtimeFile = $releaseIndex.releases[0].runtime.files |
            Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-runtime-win-x64.zip' } |
            Select-Object -First 1
        if (-not $runtimeFile -or $runtimeFile.url -notlike 'https://builds.dotnet.microsoft.com/dotnet/Runtime/*') {
            throw 'Could not resolve the official .NET 8 x64 runtime package.'
        }
        $runtimeZip = Join-Path $work 'tools\dotnet-runtime-8-win-x64.zip'
        Invoke-WebRequest $runtimeFile.url -OutFile $runtimeZip -UseBasicParsing
        $actualHash = (Get-FileHash -LiteralPath $runtimeZip -Algorithm SHA512).Hash
        if ($actualHash -ne $runtimeFile.hash) { throw 'The .NET 8 x64 runtime package hash did not match Microsoft metadata.' }
        Expand-Archive -LiteralPath $runtimeZip -DestinationPath $localDotNetRoot -Force
        $signingDotNetRoot = $localDotNetRoot
    }
    $env:DOTNET_ROOT_X64 = $signingDotNetRoot
    $signToolPath = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
        -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signToolPath) { throw 'A current x64 SignTool from Microsoft.Windows.SDK.BuildTools is required.' }

    $clientVersion = '1.0.128'
    $clientRoot = Join-Path $work "tools\artifactsigning-client-$clientVersion"
    $dlibPath = Join-Path $clientRoot 'bin\x64\Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path -LiteralPath $dlibPath)) {
        $package = Join-Path $work "tools\Microsoft.ArtifactSigning.Client.$clientVersion.nupkg"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$clientVersion/microsoft.artifactsigning.client.$clientVersion.nupkg" `
            -OutFile $package -UseBasicParsing
        $zip = "$package.zip"
        Copy-Item -LiteralPath $package -Destination $zip -Force
        Expand-Archive -LiteralPath $zip -DestinationPath $clientRoot -Force
    }
    if (-not (Test-Path -LiteralPath $dlibPath)) { throw 'Artifact Signing client dlib was not found.' }
    $metadataPath = Join-Path $work 'signing-metadata.json'
    $metadataJson = @{
        Endpoint = $SigningEndpoint.TrimEnd('/')
        CodeSigningAccountName = $SigningAccountName
        CertificateProfileName = $CertificateProfileName
        CorrelationId = "ShelfRow-$Version"
    } | ConvertTo-Json
    [IO.File]::WriteAllText($metadataPath, $metadataJson, [Text.UTF8Encoding]::new($false))
}

function Invoke-ArtifactSigning([string]$FilePath) {
    & $signToolPath sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 `
        /dlib $dlibPath /dmdf $metadataPath $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Artifact Signing failed for $FilePath ($LASTEXITCODE)" }
    Assert-ArtifactSignature $FilePath
}

function Assert-ArtifactSignature([string]$FilePath) {
    if ($SigningProfileType -eq 'PublicTrust') {
        & $signToolPath verify /pa /v $FilePath
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $FilePath ($LASTEXITCODE)" }
        return
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    $allowedPrivateTrustFailure = $signature.Status -eq 'UnknownError' -and
        $signature.StatusMessage -match 'root certificate which is not trusted'
    if (($signature.Status -ne 'Valid' -and -not $allowedPrivateTrustFailure) -or
        -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
        throw "Private Trust signature verification failed for $FilePath ($($signature.Status): $($signature.StatusMessage))"
    }
}

$publishedFolders = @()
foreach ($targetArchitecture in $Architecture) {
    $runtimeIdentifier = "win-$targetArchitecture"
    $publish = Join-Path $work ("staging\$runtimeIdentifier-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $publish | Out-Null
    $publishedFolders += $publish

    & dotnet publish (Join-Path $root 'src\ShelfRow.App\ShelfRow.App.csproj') `
        -c Release -r $runtimeIdentifier --self-contained true "-p:Platform=$targetArchitecture" -p:PublishProfile= `
        -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
        -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for ${targetArchitecture}: $LASTEXITCODE" }

    foreach ($name in @('ShelfRow.App.exe', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'WebView2Loader.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $name))) { throw "Missing $targetArchitecture runtime file: $name" }
    }

    if ($signingEnabled) {
        Get-ChildItem -LiteralPath $publish -File |
            Where-Object { $_.Name -eq 'ShelfRow.App.exe' -or $_.Name -like 'ShelfRow.*.dll' } |
            ForEach-Object { Invoke-ArtifactSigning $_.FullName }
    }

    $innoArchitecture = if ($targetArchitecture -eq 'arm64') { 'arm64' } else { 'x64os' }
    $compilerArguments = @(
        "/DAppVersion=$Version",
        "/DArchitecture=$targetArchitecture",
        "/DRuntimeIdentifier=$runtimeIdentifier",
        "/DInnoArchitecture=$innoArchitecture",
        "/DVCRuntimeArchitecture=$targetArchitecture",
        "/DPublishDir=$publish",
        "/DPrerequisiteDir=$prerequisites",
        "/DOutputPath=$dist"
    )
    if ($signingEnabled) {
        $innoSignCommand = '$q' + $signToolPath + '$q sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $q' + `
            $dlibPath + '$q /dmdf $q' + $metadataPath + '$q $f'
        $compilerArguments += '/DSignToolName=artifact'
        $compilerArguments += "/Sartifact=$innoSignCommand"
    }
    $compilerArguments += (Join-Path $PSScriptRoot 'ShelfRow.iss')
    & $IsccPath @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed for ${targetArchitecture}: $LASTEXITCODE" }

    $installer = Join-Path $dist "ShelfRow-$Version-$runtimeIdentifier-Setup.exe"
    if ($signingEnabled) {
        Assert-ArtifactSignature $installer
    }
    $hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($installer))" | Set-Content -Encoding ASCII "$installer.sha256"
    Write-Host "Installer: $installer"
    Write-Host "Published application: $publish"
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-ja.txt') -Destination $dist
if ($signingEnabled -and $SigningProfileType -eq 'PrivateTrust') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PRIVATE-TRUST-ja.txt') -Destination $dist
    if ($PrivateTrustRootCertificatePath) {
        $rootCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path $PrivateTrustRootCertificatePath))
        if ($rootCertificate.Subject -ne $rootCertificate.Issuer) { throw 'PrivateTrustRootCertificatePath is not a self-signed root certificate.' }
        $rootPath = Join-Path $dist 'ArtifactSigning-PrivateTrust-Root.cer'
        Copy-Item -LiteralPath $PrivateTrustRootCertificatePath -Destination $rootPath -Force
        $rootHash = Get-FileHash -LiteralPath $rootPath -Algorithm SHA256
        "$($rootHash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($rootPath))" | Set-Content -Encoding ASCII "$rootPath.sha256"
    } else {
        Write-Warning 'Private Trust root certificate was not supplied. Retrieve it with Get-AzArtifactSigningCertificateRoot before distribution.'
    }
}
if (-not $NoOpen) {
    $shell = New-Object -ComObject Shell.Application
    foreach ($folder in @($publishedFolders + $dist)) {
        $alreadyOpen = @($shell.Windows()) | Where-Object {
            try { $_.Document.Folder.Self.Path -eq $folder } catch { $false }
        }
        if (-not $alreadyOpen) { $shell.Open($folder) }
    }
}
