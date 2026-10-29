using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("AUX Dropout Fix")]
[assembly: AssemblyProduct("AUX Dropout Fix")]
[assembly: AssemblyDescription("Fixes AUX dropouts when your PC volume is low.")]
[assembly: AssemblyVersion("0.0.1.0")]
[assembly: AssemblyFileVersion("0.0.1.0")]
[assembly: AssemblyInformationalVersion("0.0.1")]

// DllImport (avrt.dll) only loads from the Windows system folder, never from the exe's folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
