#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $KspRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$managed = Join-Path $KspRoot 'KSP_x64_Data\Managed'
$pluginPath = Join-Path $root 'src\KSPAutoCraft\bin\Release\net472\KSPAutoCraft.dll'
$artifactRoot = Join-Path $root 'artifacts'
[void] [System.IO.Directory]::CreateDirectory($artifactRoot)
$testFolder = Join-Path $artifactRoot ('native-config-' + [Guid]::NewGuid().ToString('N'))
[void] [System.IO.Directory]::CreateDirectory($testFolder)
$files = [System.Collections.Generic.List[string]]::new()
try {
    $game = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
    $plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
    $save = $plugin.GetType('KSPAutoCraft.KspAdapter').GetMethod('SaveNew', [Reflection.BindingFlags]'NonPublic,Static')
    $node = [Activator]::CreateInstance($game.GetType('ConfigNode'))
    $node.AddValue('ship', 'Native serialization probe')
    $node.AddValue('version', '1.12.5')
    $node.AddValue('type', 'VAB')
    $part = $node.AddNode('PART')
    $part.AddValue('part', 'test_123')
    $part.AddValue('link', 'child_456')
    $part.AddValue('attN', 'bottom,child_456_0|-1|0_0|-1|0_0|-1|0_0|-1|0')
    $resource = $part.AddNode('RESOURCE')
    $resource.AddValue('name', 'LiquidFuel')
    $resource.AddValue('amount', '12.5')
    $module = $part.AddNode('MODULE')
    $module.AddValue('name', 'ModuleTest')
    $module.AddValue('field', 'preserved')
    $sentinel = Join-Path $testFolder 'existing.craft'
    [System.IO.File]::WriteAllText($sentinel, 'existing user craft')
    $files.Add($sentinel)
    $first = [string] $save.Invoke($null, [object[]] @($node, [string] $testFolder, 'candidate'))
    $files.Add($first)
    $second = [string] $save.Invoke($null, [object[]] @($node, [string] $testFolder, 'candidate'))
    $files.Add($second)
    if ($first -eq $second) { throw 'Unique-name check failed.' }
    if ([System.IO.File]::ReadAllText($sentinel) -ne 'existing user craft') { throw 'Existing file was altered.' }
    $loaded = [ConfigNode]::Load($first)
    if ($loaded.GetValue('ship') -ne 'Native serialization probe' -or $loaded.GetValue('type') -ne 'VAB') { throw 'Root-level craft fields were lost or wrapped.' }
    if ($loaded.GetNodes('PART').Length -ne 1) { throw 'PART count changed.' }
    $loadedPart = $loaded.GetNode('PART')
    if ($loadedPart.GetValue('link') -ne 'child_456' -or $loadedPart.GetValue('attN') -ne $part.GetValue('attN')) { throw 'Attachment serialization changed.' }
    if ($loadedPart.GetNode('RESOURCE').GetValue('amount') -ne '12.5') { throw 'Resource serialization changed.' }
    if ($loadedPart.GetNode('MODULE').GetValue('field') -ne 'preserved') { throw 'Module serialization changed.' }
    'Native ConfigNode integration: PASS (root, PART, links/nodes, resources, modules, unique names, existing-file preservation).'
    'This test loads the real KSP managed assembly; it does not start Unity or validate in-game craft loading.'
}
finally {
    foreach ($file in $files) { [System.IO.File]::Delete($file) }
    [System.IO.Directory]::Delete($testFolder, $false)
}
