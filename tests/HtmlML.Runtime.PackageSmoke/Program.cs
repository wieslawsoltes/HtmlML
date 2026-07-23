using System.Runtime.InteropServices;
using HtmlML.Core;
using JavaScript.Avalonia;

if (typeof(AvaloniaBrowserHost).Assembly.GetName().Name != "HtmlML.Backend.Avalonia")
{
    return Fail("The Avalonia browser host did not come from HtmlML.Backend.Avalonia.");
}
if (typeof(IHtmlMlBackendHost).Assembly.GetName().Name != "HtmlML.Core")
{
    return Fail("The backend contract did not come from HtmlML.Core.");
}

var nativeFileName = OperatingSystem.IsWindows()
    ? "htmlml_native_engine.dll"
    : OperatingSystem.IsMacOS()
        ? "libhtmlml_native_engine.dylib"
        : "libhtmlml_native_engine.so";
var nativePath = Path.Combine(AppContext.BaseDirectory, nativeFileName);
var icuPath = Path.Combine(AppContext.BaseDirectory, "icudtl.dat");
var manifestPath = Path.Combine(AppContext.BaseDirectory, "htmlml-native-runtime.json");
foreach (var required in new[] { nativePath, icuPath, manifestPath })
{
    if (!File.Exists(required))
    {
        return Fail($"The runtime package did not copy '{Path.GetFileName(required)}'.");
    }
}

var library = NativeLibrary.Load(nativePath);
try
{
    var export = NativeLibrary.GetExport(library, "htmlml_engine_get_abi_version");
    var getAbiVersion = Marshal.GetDelegateForFunctionPointer<GetAbiVersion>(export);
    var abiVersion = getAbiVersion();
    if (abiVersion != 2)
    {
        return Fail($"The native runtime reported ABI {abiVersion}; expected 2.");
    }
    Console.WriteLine(
        $"HtmlML package smoke: pass; backend={typeof(AvaloniaBrowserHost).Assembly.GetName().Name}; " +
        $"runtime={Path.GetFileName(nativePath)}; abi={abiVersion}");
}
finally
{
    NativeLibrary.Free(library);
}

return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetAbiVersion();
