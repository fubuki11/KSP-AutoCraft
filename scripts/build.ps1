#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $KspRoot,

    [ValidateNotNullOrEmpty()]
    [string] $DownloadUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $projectRoot 'src\KSPAutoCraft\KSPAutoCraft.csproj'
$versionPath = Join-Path $projectRoot 'GameData\KSPAutoCraft\KSPAutoCraft.version'
$distPath = Join-Path $projectRoot 'dist'

if ($PSBoundParameters.ContainsKey('DownloadUrl')) {
    $downloadUri = $null
    if (-not [System.Uri]::IsWellFormedUriString($DownloadUrl, [System.UriKind]::Absolute) -or
        -not [System.Uri]::TryCreate($DownloadUrl, [System.UriKind]::Absolute, [ref] $downloadUri) -or
        $downloadUri.Scheme -ne 'https' -or -not $downloadUri.Host -or $downloadUri.Port -lt 1 -or
        $downloadUri.UserInfo -or $downloadUri.Fragment) {
        throw '-DownloadUrl must be an absolute HTTPS URL without credentials or a fragment.'
    }
}

$kspDirectory = Get-Item -LiteralPath $KspRoot
if ($kspDirectory -isnot [System.IO.DirectoryInfo]) {
    throw '-KspRoot must be a filesystem directory.'
}
$resolvedKspRoot = $kspDirectory.FullName
$managedAssembly = Join-Path $resolvedKspRoot 'KSP_x64_Data\Managed\Assembly-CSharp.dll'
if (-not (Test-Path -LiteralPath $managedAssembly -PathType Leaf)) {
    throw "KSP assemblies not found under '$resolvedKspRoot'. Supply the KSP 1.12.5 game root."
}

$versionData = [System.IO.File]::ReadAllText($versionPath) | ConvertFrom-Json
$versions = @{}
foreach ($key in @('VERSION', 'KSP_VERSION')) {
    $parts = foreach ($part in @('MAJOR', 'MINOR', 'PATCH')) {
        $number = $versionData.$key.$part
        if (($number -isnot [int] -and $number -isnot [long]) -or $number -lt 0) {
            throw "$key.$part in KSPAutoCraft.version must be a nonnegative integer."
        }
        [string] $number
    }
    $versions[$key] = $parts -join '.'
}
$version = $versions['VERSION']
$packageName = "KSPAutoCraft-$version"
$zipPath = Join-Path $distPath "$packageName.zip"
$ckanPath = Join-Path $distPath "$packageName.ckan"
$clientZipPath = Join-Path $distPath "KSPAutoCraft-client-$version.zip"
$repositoryZipPath = Join-Path $distPath 'KSPAutoCraft-local-repository.zip'
$dllPath = Join-Path $projectRoot 'src\KSPAutoCraft\bin\Release\net472\KSPAutoCraft.dll'

$gameFiles = @{
    'GameData/KSPAutoCraft/Plugins/KSPAutoCraft.dll' = $dllPath
    'GameData/KSPAutoCraft/KSPAutoCraft.version' = $versionPath
    'GameData/KSPAutoCraft/LICENSE' = Join-Path $projectRoot 'LICENSE'
    'GameData/KSPAutoCraft/README.md' = Join-Path $projectRoot 'README.md'
}
$pythonModules = @('__init__.py', '__main__.py', 'client.py', 'physics.py', 'performance.py', 'llm.py', 'contracts.py', 'designer.py', 'desktop.py', 'gateway.py')
foreach ($name in $pythonModules) {
    $gameFiles["GameData/KSPAutoCraft/Client/ksp_autocraft/$name"] = Join-Path $projectRoot "client/ksp_autocraft/$name"
}
# Explicit source manifests never collect runtime settings, secrets, caches or egg metadata.
$clientFiles = @{}
foreach ($relativePath in @(
    'client/pyproject.toml',
    'client/ksp_autocraft/__init__.py',
    'client/ksp_autocraft/__main__.py',
    'client/ksp_autocraft/client.py',
    'client/ksp_autocraft/physics.py',
    'client/ksp_autocraft/performance.py',
    'client/ksp_autocraft/llm.py',
    'client/ksp_autocraft/contracts.py',
    'client/ksp_autocraft/designer.py',
    'client/ksp_autocraft/desktop.py',
    'client/ksp_autocraft/gateway.py',
    'examples/starter-stack.json',
    'docs/AIHUB.md',
    'docs/API.md',
    'docs/NATURAL-DESIGN.md',
    'docs/GAME-UI.md',
    'docs/PERFORMANCE.md',
    'docs/craft-plan.schema.json',
    'docs/CKAN-IMPORT-FIX.md',
    'README.md',
    'LICENSE'
)) {
    $clientFiles[$relativePath] = Join-Path $projectRoot $relativePath
}
foreach ($source in @($projectPath, $versionPath) + @($clientFiles.Values)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required packaging source is missing: $source"
    }
}

# Do not follow output junctions/symlinks into a game installation or another directory.
if (Test-Path -LiteralPath $distPath) {
    $dist = Get-Item -LiteralPath $distPath -Force
    if ($dist -isnot [System.IO.DirectoryInfo] -or
        ($dist.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Output directory must be a regular directory: $distPath"
    }
}
foreach ($destination in @($zipPath, $ckanPath, $clientZipPath, $repositoryZipPath)) {
    if (Test-Path -LiteralPath $destination) {
        $item = Get-Item -LiteralPath $destination -Force
        if ($item -isnot [System.IO.FileInfo] -or
            ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Output must be a regular file: $destination"
        }
        try {
            # Detect a CKAN/ZIP viewer handle before compiling or replacing outputs.
            $probe = [System.IO.File]::Open($destination, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            $probe.Dispose()
        }
        catch {
            throw "Release output is in use or unreadable: $destination. Close CKAN/ZIP viewers, then retry. Existing packages were not changed."
        }
    }
}

$dotnet = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
& $dotnet build $projectPath --configuration Release --framework net472 "/p:KspRoot=$resolvedKspRoot" "/p:Version=$version"
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    [Console]::Error.WriteLine("dotnet build failed with exit code $buildExitCode; packages were not updated.")
    exit $buildExitCode
}
if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    throw "Build did not produce the expected DLL: $dllPath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-PackageArchive {
    param([string] $Path, [hashtable] $Files)

    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [string[]] $names = @($Files.Keys)
        [System.Array]::Sort($names, [System.StringComparer]::Ordinal)
        foreach ($name in $names) {
            $entry = $archive.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            # ZIP timestamps are fixed so identical inputs yield identical archives.
            $entry.LastWriteTime = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
            $sourceStream = [System.IO.File]::OpenRead($Files[$name])
            try {
                $entryStream = $entry.Open()
                try {
                    $sourceStream.CopyTo($entryStream)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
            finally {
                $sourceStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

if ($PSBoundParameters.ContainsKey('DownloadUrl')) {
    $download = $downloadUri.AbsoluteUri
}
else {
    $download = [System.Uri]::new($zipPath).AbsoluteUri
}
$metadata = [ordered] @{
    spec_version = 1
    identifier = 'KSPAutoCraft'
    name = 'KSP AutoCraft'
    author = 'fubuki11st'
    abstract = 'Editor-only craft planning and generation with a local Python client.'
    license = 'MIT'
    version = $version
    ksp_version = $versions['KSP_VERSION']
    download = $download
    depends = @(@{ name = 'KSPAIHub'; min_version = '0.3.0' })
    install = @(@{ file = 'GameData/KSPAutoCraft'; install_to = 'GameData' })
}

[void] [System.IO.Directory]::CreateDirectory($distPath)
$stagePath = Join-Path $distPath ('.package-' + [System.Guid]::NewGuid().ToString('N'))
[void] [System.IO.Directory]::CreateDirectory($stagePath)
$stagedCkan = Join-Path $stagePath 'KSPAutoCraft.ckan'
$stagedZip = Join-Path $stagePath "$packageName.zip"
$stagedClientZip = Join-Path $stagePath "KSPAutoCraft-client-$version.zip"
$stagedRepositoryZip = Join-Path $stagePath 'KSPAutoCraft-local-repository.zip'
try {
    $json = ($metadata | ConvertTo-Json -Depth 5) + "`n"
    [System.IO.File]::WriteAllText($stagedCkan, $json, [System.Text.UTF8Encoding]::new($false))
    $gameFiles['KSPAutoCraft.ckan'] = $stagedCkan
    Write-PackageArchive -Path $stagedZip -Files $gameFiles
    Write-PackageArchive -Path $stagedClientZip -Files $clientFiles
    Write-PackageArchive -Path $stagedRepositoryZip -Files @{ "KSPAutoCraft/$packageName.ckan" = $stagedCkan }

    Move-Item -LiteralPath $stagedZip -Destination $zipPath -Force
    Move-Item -LiteralPath $stagedCkan -Destination $ckanPath -Force
    Move-Item -LiteralPath $stagedClientZip -Destination $clientZipPath -Force
    Move-Item -LiteralPath $stagedRepositoryZip -Destination $repositoryZipPath -Force
}
finally {
    # Only delete owned staging files, never recursively remove a directory.
    foreach ($stagedFile in @($stagedCkan, $stagedZip, $stagedClientZip, $stagedRepositoryZip)) {
        [System.IO.File]::Delete($stagedFile)
    }
    [System.IO.Directory]::Delete($stagePath, $false)
}

$zipPath
$ckanPath
$clientZipPath
$repositoryZipPath
