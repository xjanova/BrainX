# Who-Holds.ps1 - name the processes using a file, via the Windows Restart Manager.
# This is the same API Velopack uses for "Checking for running processes in: ...".
# It ANSWERS the question directly instead of narrowing it by elimination.
#
# Usage: .\Who-Holds.ps1 -Path 'C:\path\to\file-or-files'
param([Parameter(Mandatory)][string[]]$Path)

$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class RmApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    const int CCH_RM_MAX_APP_NAME = 255;
    const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

    public static List<string> Holders(string[] files)
    {
        var result = new List<string>();
        uint session; var key = Guid.NewGuid().ToString();
        if (RmStartSession(out session, 0, key) != 0) { result.Add("RmStartSession failed"); return result; }
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
            { result.Add("RmRegisterResources failed"); return result; }

            uint pnProcInfo = 0, needed = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref pnProcInfo, null, ref reasons);
            if (rc == 234 && needed > 0)   // ERROR_MORE_DATA
            {
                var info = new RM_PROCESS_INFO[needed];
                pnProcInfo = needed;
                if (RmGetList(session, out needed, ref pnProcInfo, info, ref reasons) == 0)
                    for (int i = 0; i < pnProcInfo; i++)
                        result.Add(info[i].Process.dwProcessId + "\t" + info[i].strAppName);
            }
            else if (rc != 0) result.Add("RmGetList rc=" + rc);
        }
        finally { RmEndSession(session); }
        return result;
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp | Out-Null

$files = @()
foreach ($p in $Path) {
    if (Test-Path -LiteralPath $p -PathType Container) {
        $files += (Get-ChildItem -LiteralPath $p -File -Recurse -ErrorAction SilentlyContinue |
                   Select-Object -First 400).FullName
    } elseif (Test-Path -LiteralPath $p) { $files += (Resolve-Path -LiteralPath $p).Path }
}
if ($files.Count -eq 0) { Write-Output 'no files to register'; exit 1 }

Write-Output ("registered {0} file(s); asking the Restart Manager who is using them..." -f $files.Count)
$holders = [RmApi]::Holders($files)
if ($holders.Count -eq 0) { Write-Output 'RM reports NOBODY holding these files.' }
else { $holders | ForEach-Object { Write-Output ('  ' + $_) } }
