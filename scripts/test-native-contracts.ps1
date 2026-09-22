#requires -Version 5.1
param([Parameter(Mandatory = $true)][string] $KspRoot)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $KspRoot 'KSP_x64_Data\Managed\Assembly-CSharp.dll'))
$fields = @{
    'FinePrint.Contracts.Parameters.CrewCapacityParameter' = @('targetCapacity')
    'Contracts.Parameters.PartTest' = @('tgtPartInfo', 'hauled', 'body', 'situation')
    'FinePrint.Contracts.Parameters.VesselSystemsParameter' = @('checkModuleTypes', 'mannedStatus', 'requireNew')
    'FinePrint.Contracts.Parameters.PartRequestParameter' = @('partNames', 'moduleNames')
    'FinePrint.Contracts.Parameters.ResourcePossessionParameter' = @('resourceName', 'goalResource')
    'Contracts.Parameters.ReachFlightEnvelope' = @('Destination', 'Situation', 'minAltitude', 'maxAltitude', 'minSpeed', 'maxSpeed')
    'FinePrint.Contracts.Parameters.SpecificOrbitParameter' = @('sma', 'inclination', 'eccentricity', 'lan', 'argumentOfPeriapsis', 'meanAnomalyAtEpoch', 'epoch', 'TargetBody')
}
$count = 0
foreach ($typeName in $fields.Keys) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($name in $fields[$typeName]) {
        if ($null -eq $type.GetField($name, [Reflection.BindingFlags]'Instance,Public,NonPublic')) { throw "Missing supported contract field: $typeName.$name" }
        $count++
    }
}
"Native KSP contract field layout: $count checks passed. Runtime contract instances still require game validation."
