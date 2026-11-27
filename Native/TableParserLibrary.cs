using System.Reflection;
using System.Runtime.InteropServices;
using Com.Dowjones.Ucpp.Mesh.TableParserWrapper;

namespace TableParseFeedTest.Native;

/// <summary>
/// Points ucpp-parser-table-parse at the libTableParser.so under test.
///
/// The wrapper's DllImports name the absolute path the production image installs to. A
/// DllImportResolver on the wrapper assembly hands every one of them the handle of the library under
/// test instead, so the wrapper code itself runs unmodified.
/// </summary>
public static class TableParserLibrary
{
    public const string WrapperLibraryPath = "/usr/local/lib/libTableParser.so";

    private static IntPtr _handle;

    public static string LoadedPath { get; private set; } = string.Empty;

    /// <summary>
    /// PARSER_CONFIG_PATH / PARSER_DATABASE_PATH for both engines. The native parser reads them with getenv().
    /// Environment.SetEnvironmentVariable only changes .NET's own copy of the environment on Unix, so set them in libc too.
    /// </summary>
    public static void SetEnvironment(string configPath, string databasePath)
    {
        SetNativeEnvironment("PARSER_CONFIG_PATH", configPath);
        SetNativeEnvironment("PARSER_DATABASE_PATH", databasePath);
    }

    public static void Load(string libraryPath, string configPath, string databasePath)
    {
        SetEnvironment(configPath, databasePath);

        // ParserTableParse.ResetTableParse dlopen()s WrapperLibraryPath itself, which no resolver can
        // redirect. A symlink to the library under test makes dlopen return this same handle; anything
        // else there would load a second, different library into the process.
        if (File.Exists(WrapperLibraryPath) && RealPath(WrapperLibraryPath) != RealPath(libraryPath))
        {
            throw new InvalidOperationException(
                $"{WrapperLibraryPath} resolves to {RealPath(WrapperLibraryPath)}, not the library under test " +
                $"{RealPath(libraryPath)}. Point it at the library under test: " +
                $"sudo ln -sfn {RealPath(libraryPath)} {WrapperLibraryPath}");
        }

        _handle = NativeLibrary.Load(libraryPath);
        LoadedPath = libraryPath;

        NativeLibrary.SetDllImportResolver(typeof(ParserTableParse).Assembly, Resolve);
    }

    /// <summary>
    /// The native <c>Reset</c>. ParserTableParse.ResetTableParse only reaches it after re-opening
    /// <see cref="WrapperLibraryPath"/> from disk, which fails when nothing is installed there.
    /// </summary>
    public static void Reset()
    {
        var reset = Marshal.GetDelegateForFunctionPointer<ResetDelegate>(NativeLibrary.GetExport(_handle, "Reset"));
        reset();
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        => libraryName == WrapperLibraryPath ? _handle : IntPtr.Zero;

    private static string RealPath(string path)
        => new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);

    private static void SetNativeEnvironment(string name, string value)
    {
        if (setenv(name, value, 1) != 0)
        {
            throw new InvalidOperationException($"setenv({name}) failed: errno {Marshal.GetLastWin32Error()}");
        }
        Environment.SetEnvironmentVariable(name, value);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResetDelegate();

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);
}
