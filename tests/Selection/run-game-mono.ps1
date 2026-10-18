param(
    [Parameter(Mandatory=$true)][string]$GamePackage
)
$ErrorActionPreference = 'Stop'
$game = [IO.Path]::GetFullPath($GamePackage)
$output = Join-Path $PSScriptRoot 'bin/Release/net472'
$test = Join-Path $output 'Selection.Tests.exe'
$library = Join-Path $game 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'
if (!(Test-Path -LiteralPath $test) -or !(Test-Path -LiteralPath $library)) { throw 'Build tests and provide the actual Mono game package first.' }
$arguments = @()
# Run in a dedicated PowerShell process. Only test assemblies are opened, not Sinmai.exe.
$source = @'
using System;
using System.Runtime.InteropServices;
public static class GameMonoTestHost {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryW(string path);
    const string Mono="mono-2.0-bdwgc.dll";
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)]
    static extern void mono_set_assemblies_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)]
    static extern IntPtr mono_jit_init_version(string name,string version);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)]
    static extern IntPtr mono_domain_assembly_open(IntPtr domain,[MarshalAs(UnmanagedType.LPUTF8Str)] string file);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)]
    static extern int mono_jit_exec(IntPtr domain,IntPtr assembly,int argc,IntPtr argv);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)]
    static extern IntPtr mono_get_runtime_build_info();
    public static int Run(string library,string managed,string file,string[] arguments) {
        if(LoadLibraryW(library)==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Console.WriteLine("Game Mono: " + Marshal.PtrToStringAnsi(mono_get_runtime_build_info()));
        mono_set_assemblies_path(managed);
        var domain=mono_jit_init_version("AlphaRegression","v4.0.30319");
        if(domain==IntPtr.Zero)throw new Exception("Mono initialization failed");
        var assembly=mono_domain_assembly_open(domain,file);
        if(assembly==IntPtr.Zero)throw new Exception("Test assembly load failed");
        var values=new IntPtr[arguments.Length+1];
        values[0]=Marshal.StringToCoTaskMemUTF8(file);
        for(int i=0;i<arguments.Length;i++)values[i+1]=Marshal.StringToCoTaskMemUTF8(arguments[i]);
        var argv=Marshal.AllocHGlobal(IntPtr.Size*values.Length);
        try {
            Marshal.Copy(values,0,argv,values.Length);
            return mono_jit_exec(domain,assembly,values.Length,argv);
        } finally {
            Marshal.FreeHGlobal(argv);
            foreach(var value in values)Marshal.FreeCoTaskMem(value);
        }
    }
}
'@
Add-Type -TypeDefinition $source
exit [GameMonoTestHost]::Run($library, (Join-Path $game 'Sinmai_Data/Managed'), $test, [string[]]$arguments)
