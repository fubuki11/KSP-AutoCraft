#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $KspRoot = 'D:\steam\steamapps\common\Kerbal Space Program',
    [string] $CkanPath,
    [string] $Archive,
    [string] $Metadata,
    [switch] $Upgrade
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$versionData = [System.IO.File]::ReadAllText((Join-Path $projectRoot 'GameData\KSPAutoCraft\KSPAutoCraft.version')) | ConvertFrom-Json
$version = '{0}.{1}.{2}' -f $versionData.VERSION.MAJOR, $versionData.VERSION.MINOR, $versionData.VERSION.PATCH
if (-not $Archive) { $Archive = Join-Path $projectRoot "dist\KSPAutoCraft-$version.zip" }
if (-not $Metadata) { $Metadata = Join-Path $projectRoot "dist\KSPAutoCraft-$version.ckan" }
if (-not $CkanPath) { $CkanPath = Join-Path $KspRoot 'CKAN\ckan-windows.exe' }
$KspRoot = [System.IO.Path]::GetFullPath($KspRoot)
$Archive = [System.IO.Path]::GetFullPath($Archive)
$Metadata = [System.IO.Path]::GetFullPath($Metadata)
$CkanPath = [System.IO.Path]::GetFullPath($CkanPath)
foreach ($file in @($Archive, $Metadata, $CkanPath)) {
    if (-not [System.IO.File]::Exists($file)) { throw "Required file not found: $file" }
}
if (-not [System.IO.Directory]::Exists((Join-Path $KspRoot 'GameData'))) { throw 'KspRoot must contain GameData.' }
$registryPath = Join-Path $KspRoot 'CKAN\registry.json'
$lockPath = Join-Path $KspRoot 'CKAN\registry.locked'
if ([System.IO.File]::Exists($lockPath)) {
    try {
        $probe = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        $probe.Dispose()
    }
    catch { throw 'CKAN is using this instance. Exit its GUI normally, then run install-local.ps1 again.' }
}

$module = [System.IO.File]::ReadAllText($Metadata) | ConvertFrom-Json
if ($module.identifier -ne 'KSPAutoCraft' -or $module.version -ne $version) { throw 'Unexpected package identifier or version.' }
if (@($module.install).Count -ne 1 -or $module.install[0].file -ne 'GameData/KSPAutoCraft' -or $module.install[0].install_to -ne 'GameData') {
    throw 'Unexpected CKAN install mapping.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$dllRelative = 'GameData/KSPAutoCraft/Plugins/KSPAutoCraft.dll'
$payloadFiles = @(
    $dllRelative, 'GameData/KSPAutoCraft/KSPAutoCraft.version',
    'GameData/KSPAutoCraft/LICENSE', 'GameData/KSPAutoCraft/README.md'
)
$zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $clientEntries = @($zip.Entries | Where-Object { $_.FullName.StartsWith('GameData/KSPAutoCraft/Client/ksp_autocraft/') -and $_.FullName.EndsWith('.py') })
    foreach ($entry in $clientEntries) {
        if ($entry.FullName -match '\.\.|\\|:') { throw 'Invalid bundled client path.' }
        $payloadFiles += $entry.FullName
    }
    foreach ($name in $payloadFiles) {
        if ($null -eq $zip.GetEntry($name)) { throw "Missing install payload: $name" }
    }
    $stream = $zip.GetEntry($dllRelative).Open()
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try { $expectedHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
}
finally { $zip.Dispose() }

function Property-Value($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-VerifiedInstallation {
    if (-not [System.IO.File]::Exists($registryPath)) { return $false }
    $registry = [System.IO.File]::ReadAllText($registryPath) | ConvertFrom-Json
    $installed = Property-Value (Property-Value $registry 'installed_modules') 'KSPAutoCraft'
    if ($null -eq $installed) { return $false }
    $source = Property-Value $installed 'source_module'
    if ((Property-Value $source 'version') -ne $version) {
        if (-not $Upgrade) { throw 'A different KSPAutoCraft version is registered. Close KSP/CKAN and rerun with -Upgrade to upgrade through CKAN.' }
        if ([version](Property-Value $source 'version') -ge [version]$version) { throw 'This wrapper does not downgrade an installed release.' }
        $script:NeedsUpgrade = $true
        return $false
    }
    $owners = Property-Value $registry 'installed_files'
    $moduleFiles = Property-Value $installed 'installed_files'
    foreach ($relative in $payloadFiles) {
        if ((Property-Value $owners $relative) -ne 'KSPAutoCraft' -or
            $null -eq (Property-Value $moduleFiles $relative) -or
            -not [System.IO.File]::Exists((Join-Path $KspRoot $relative))) {
            throw "Registered installation is incomplete: $relative. Use CKAN to repair/reinstall it."
        }
    }
    $installedVersion = [System.IO.File]::ReadAllText((Join-Path $KspRoot 'GameData\KSPAutoCraft\KSPAutoCraft.version')) | ConvertFrom-Json
    $actualVersion = '{0}.{1}.{2}' -f $installedVersion.VERSION.MAJOR, $installedVersion.VERSION.MINOR, $installedVersion.VERSION.PATCH
    if ($actualVersion -ne $version) { throw 'Installed .version does not match the release.' }
    $stream = [System.IO.File]::OpenRead((Join-Path $KspRoot $dllRelative))
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
    if ($actualHash -ne $expectedHash) {
        throw 'Installed DLL differs from this ZIP. Bump the release version for changed builds; do not overwrite CKAN-managed files.'
    }
    foreach ($relative in $payloadFiles | Where-Object { $_.EndsWith('.py') }) {
        $archiveCheck = [System.IO.Compression.ZipFile]::OpenRead($Archive)
        $installedStream = $null; $archiveStream = $null; $hasher = [System.Security.Cryptography.SHA256]::Create()
        try {
            $archiveStream = $archiveCheck.GetEntry($relative).Open()
            $expected = [BitConverter]::ToString($hasher.ComputeHash($archiveStream))
            $installedStream = [System.IO.File]::OpenRead((Join-Path $KspRoot $relative))
            $actual = [BitConverter]::ToString($hasher.ComputeHash($installedStream))
            if ($actual -ne $expected) { throw "Bundled client file differs from the release: $relative" }
        }
        finally {
            if ($installedStream) { $installedStream.Dispose() }; if ($archiveStream) { $archiveStream.Dispose() }
            $hasher.Dispose(); $archiveCheck.Dispose()
        }
    }
    Write-Information -MessageData "Verified: CKAN owns $($payloadFiles.Count) payload files; version=$version; DLL SHA256=$actualHash" -InformationAction Continue
    return $true
}

function Initialize-DesktopSettings {
    if ($clientEntries.Count -eq 0) { return }
    $path = Join-Path $KspRoot 'GameData\KSPAutoCraft\PluginData\desktop.json'
    if ([System.IO.File]::Exists($path)) {
        $old = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json
        $schema = Property-Value $old 'schemaVersion'
        if ($schema -notin @(1, 2)) { throw 'Unsupported desktop.json schema; existing settings were not overwritten.' }
        foreach ($property in $old.PSObject.Properties) {
            if ($property.Name -notin @('schemaVersion', 'pythonExecutable', 'autoEnableGeneration', 'modelConfig', 'autoCheckOnEnter')) {
                throw 'Unknown desktop.json field; existing settings were not overwritten.'
            }
        }
        $python = Property-Value $old 'pythonExecutable'
        $allow = Property-Value $old 'autoEnableGeneration'
        if ($null -ne $python -and $python -isnot [string]) { throw 'Invalid Python executable setting.' }
        if ($null -ne $allow -and $allow -isnot [bool]) { throw 'Invalid generation preference.' }
        if ($schema -eq 2 -and $null -eq $old.PSObject.Properties['modelConfig'] -and $null -eq $old.PSObject.Properties['autoCheckOnEnter']) { return }
        $profile = [ordered] @{ schemaVersion = 2; pythonExecutable = [string]$python; autoEnableGeneration = if ($null -eq $allow) { $true } else { $allow } }
        $backupFolder = Join-Path ([System.IO.Path]::GetDirectoryName($path)) 'Backups'
        [void][System.IO.Directory]::CreateDirectory($backupFolder)
        $backup = Join-Path $backupFolder ('desktop-before-aihub-' + [Guid]::NewGuid().ToString('N') + '.json')
        $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [System.IO.File]::WriteAllText($temporary, ($profile | ConvertTo-Json) + "`n", [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::Replace($temporary, $path, $backup)
        }
        finally { if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) } }
        "Desktop settings migrated to AI Hub-only schema 2: $path"
        return
    }
    $pythonPath = ''
    $commands = @(Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Sort-Object @{ Expression = { $_.Source -like '*\Microsoft\WindowsApps\*' } })
    foreach ($command in $commands) {
        try {
            $pythonInfoText = & $command.Source -c "import json,sys; print(json.dumps({'executable':sys.executable,'supported':sys.version_info >= (3,10)}))"
            if ($LASTEXITCODE -eq 0) {
                $pythonInfo = $pythonInfoText | ConvertFrom-Json
                if ($pythonInfo.supported -and [System.IO.File]::Exists([string] $pythonInfo.executable)) { $pythonPath = [string] $pythonInfo.executable; break }
            }
        }
        catch { $pythonPath = '' }
    }
    $profile = [ordered] @{ schemaVersion = 2; pythonExecutable = $pythonPath; autoEnableGeneration = $true }
    [void] [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path))
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write(($profile | ConvertTo-Json) + "`n") } finally { $writer.Dispose() }
    "Desktop startup settings created: $path"
    if (-not $pythonPath) { 'Python 3.10+ was not resolved; set its full executable path in the in-game Settings panel.' }
}

function Ensure-LocalRepository {
    $repositoryPath = Join-Path ([System.IO.Path]::GetDirectoryName($Archive)) 'KSPAutoCraft-local-repository.zip'
    if (-not [System.IO.File]::Exists($repositoryPath)) { throw 'Local repository ZIP is missing. Run scripts/build.ps1 first.' }
    $repoZip = [System.IO.Compression.ZipFile]::OpenRead($repositoryPath)
    try {
        $entry = $repoZip.GetEntry("KSPAutoCraft/KSPAutoCraft-$version.ckan")
        if ($null -eq $entry -or $repoZip.Entries.Count -ne 1) { throw 'Unexpected local repository contents. Rebuild the release.' }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $repoModule = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($repoModule.identifier -ne 'KSPAutoCraft' -or $repoModule.version -ne $version -or $repoModule.download -ne $module.download) {
            throw 'Local repository metadata is stale or the release was moved. Rebuild at the current location.'
        }
    }
    finally { $repoZip.Dispose() }
    $repositoryUri = [System.Uri]::new($repositoryPath).AbsoluteUri
    $registry = if ([System.IO.File]::Exists($registryPath)) { [System.IO.File]::ReadAllText($registryPath) | ConvertFrom-Json } else { $null }
    $repositories = Property-Value $registry 'sorted_repositories'
    if ($null -eq $repositories) { $repositories = Property-Value $registry 'repositories' }
    $existing = Property-Value $repositories 'KSPAutoCraft-local'
    if ($null -ne $existing -and (Property-Value $existing 'uri') -ne $repositoryUri) {
        throw 'KSPAutoCraft-local already points to another location. Adjust that repository in CKAN before installing here.'
    }
    if ($null -eq $existing) {
        & $CkanPath repo add KSPAutoCraft-local $repositoryUri --gamedir $KspRoot --headless
        if ($LASTEXITCODE -ne 0) { throw "CKAN repository registration failed with exit code $LASTEXITCODE." }
    }
    # Only this project's local index is refreshed, not the user's other repositories.
    & $CkanPath update --urls $repositoryUri --game KSP --gamedir $KspRoot --force --headless
    if ($LASTEXITCODE -ne 0) { throw "CKAN local repository refresh failed with exit code $LASTEXITCODE." }
}

$script:NeedsUpgrade = $false
if (Test-VerifiedInstallation) {
    Initialize-DesktopSettings
    'KSPAutoCraft is already installed and verified; no installation command was run.'
    return
}

# Resolve the archive at its current location, so moving the source folder is supported.
# Only CKAN writes the game's files and registry. This wrapper never edits registry.json.
$module.download = [System.Uri]::new($Archive).AbsoluteUri
foreach ($process in @(Get-Process KSP_x64 -ErrorAction SilentlyContinue)) {
    if ($process.Path -and $process.Path.StartsWith($KspRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'KSP is running from this game directory. Save your game and exit before installing/upgrading the plugin.'
    }
}
$temporaryManifest = Join-Path ([System.IO.Path]::GetDirectoryName($Archive)) ('.autocraft-install-' + [Guid]::NewGuid().ToString('N') + '.ckan')
try {
    $json = $module | ConvertTo-Json -Depth 20
    $stream = [System.IO.File]::Open($temporaryManifest, [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write($json) } finally { $writer.Dispose() }
    Ensure-LocalRepository
    if ($script:NeedsUpgrade) {
        # CKAN 1.36.4 --ckanfile only extracts the identifier and ignores the file's version.
        & $CkanPath upgrade "KSPAutoCraft=$version" --gamedir $KspRoot --no-recommends --headless
    }
    else {
        & $CkanPath install --gamedir $KspRoot --ckanfiles $temporaryManifest --no-recommends --headless
    }
    if ($LASTEXITCODE -ne 0) { throw "CKAN installation failed with exit code $LASTEXITCODE." }
    if (-not (Test-VerifiedInstallation)) { throw 'CKAN returned success but KSPAutoCraft is not registered as installed.' }
    Initialize-DesktopSettings
    'KSPAutoCraft installation completed and verified. Reopen CKAN and select the Installed filter.'
}
finally { [System.IO.File]::Delete($temporaryManifest) }
