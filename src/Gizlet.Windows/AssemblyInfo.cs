using System.Runtime.InteropServices;

// The flash helper runs elevated from the user's (writable) install folder: native libraries are
// only ever loaded from System32.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
