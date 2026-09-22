#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root 'src\KSPAutoCraft\bin\Release\net472\KSPAutoCraft.dll'))
$summaryType = $assembly.GetType('KSPAutoCraft.ContractSummary', $true)
$pageType = $assembly.GetType('KSPAutoCraft.ContractPage', $true)
$summary = [Activator]::CreateInstance($summaryType, $true)
$summaryType.GetField('id').SetValue($summary, '11111111-1111-1111-1111-111111111111')
$summaryType.GetField('title').SetValue($summary, 'Contract probe')
$items = [Array]::CreateInstance($summaryType, 1)
$items.SetValue($summary, 0)
$page = [Activator]::CreateInstance($pageType, $true)
$pageType.GetField('total').SetValue($page, [int] 1)
$pageType.GetField('contracts').SetValue($page, $items)
$serializer = $assembly.GetType('KSPAutoCraft.ApiJson', $true).GetMethod('Serialize', [Reflection.BindingFlags]'Static,NonPublic')
$json = [string] $serializer.Invoke($null, [object[]] @($page))
$parsed = $json | ConvertFrom-Json
if ($parsed.total -ne 1 -or @($parsed.contracts).Count -ne 1 -or $parsed.contracts[0].title -ne 'Contract probe') {
    throw 'Actual net472 contract DTO nesting was lost in serialization.'
}
'Native net472 API JSON: PASS (actual internal ContractPage and ContractSummary DTO array).'
