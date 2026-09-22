#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $Archive = (Join-Path $PSScriptRoot '..\dist\KSPAutoCraft-0.1.0.zip'),
    [string] $KspRoot = 'D:\steam\steamapps\common\Kerbal Space Program'
)

$ErrorActionPreference = 'Stop'
# Restart Manager only queries ownership here; no shutdown or restart is requested.
if (-not ('AutoCraftImportDiagnostics.Locks' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace AutoCraftImportDiagnostics {
    [StructLayout(LayoutKind.Sequential)] public struct UniqueProcess {
        public int Id;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct ProcessInfo {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Service;
        public uint AppType;
        public uint Status;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    public static class Locks {
        [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint apps, IntPtr processes, uint services, string[] serviceNames);
        [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] info, ref uint reasons);
        [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
        public static string[] Owners(string path) {
            uint session;
            int result = RmStartSession(out session, 0, new StringBuilder(33));
            if (result != 0) return new[] { "RmStartSession error " + result };
            try {
                result = RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null);
                if (result != 0) return new[] { "RmRegisterResources error " + result };
                uint needed, count=0, reasons=0;
                result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result == 0) return new string[0];
                if (result != 234) return new[] { "RmGetList error " + result };
                for (int retry=0; retry<3; retry++) {
                    var items=new ProcessInfo[needed];
                    count=needed;
                    result=RmGetList(session, out needed, ref count, items, ref reasons);
                    if (result==234) continue;
                    if (result!=0) return new[] { "RmGetList error " + result };
                    var names=new string[count];
                    for (int i=0; i<count; i++) names[i]=items[i].Process.Id + ": " + items[i].Name;
                    return names;
                }
                return new[] { "Process list changed during diagnosis" };
            } finally { RmEndSession(session); }
        }
    }
}
'@
}

$paths = @(
    [System.IO.Path]::GetFullPath($Archive),
    (Join-Path $KspRoot 'CKAN\registry.json'),
    (Join-Path $KspRoot 'CKAN\registry.locked')
)
$results = foreach ($path in $paths) {
    $exists = [System.IO.File]::Exists($path)
    $readable = $null
    $exclusive = $null
    $owners = @()
    if ($exists) {
        foreach ($mode in @('ReadShared', 'ExclusiveProbe')) {
            try {
                $share = if ($mode -eq 'ReadShared') { [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete } else { [System.IO.FileShare]::None }
                $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
                $stream.Dispose()
                $message = 'OK'
            }
            catch { $message = $_.Exception.GetBaseException().Message }
            if ($mode -eq 'ReadShared') { $readable = $message } else { $exclusive = $message }
        }
        $owners = @([AutoCraftImportDiagnostics.Locks]::Owners($path))
    }
    [pscustomobject]@{ Path = $path; Exists = $exists; ReadShared = $readable; ExclusiveProbe = $exclusive; Owners = $owners }
}
$results | ConvertTo-Json -Depth 4
