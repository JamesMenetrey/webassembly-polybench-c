#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

// ---
// Configuration of PolyBench/C.
// ---
const string topLevelFileName = "README";
string[] benchmarkFolders = {
    "datamining", "linear-algebra", "medley", "stencils"
};

// ---
// Find the root of PolyBench/C.
// ---
var polyBenchDir = new DirectoryInfo(Directory.GetCurrentDirectory());
while (!File.Exists(Path.Combine(polyBenchDir!.FullName, topLevelFileName)))
{
    polyBenchDir = polyBenchDir.Parent;

    if (polyBenchDir == null)
    {
        Console.WriteLine($"error: cannot find the root of PolyBench/C. Is the file '{topLevelFileName}' missing?");
        return -1;
    }
}

// ---
// Arguments parsing.
// ---
void PrintUsage()
{
    Console.WriteLine("dotnet run -- [--output /path/to/output] [--sgx] [--aot] [--wasi-sdk /absolute/path/to/wasi-sdk] [--wamr /absolute/path/to/wamr]");
    Environment.Exit(-1);
}

string? compilerOutputPath = null;
bool isCompiledForSgx = false;
bool isAoTCompiled = false;
string wasiSdkDir = "/opt/wasi-sdk";
string wamrDir = "/opt/wamr-sdk";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output":
            compilerOutputPath = args[++i];
            if (!Path.IsPathRooted(compilerOutputPath))
            {
                compilerOutputPath = Path.Combine(polyBenchDir.FullName, compilerOutputPath);
                Directory.CreateDirectory(compilerOutputPath);
            }
            break;
        case "--sgx":
            isCompiledForSgx = true;
            break;
        case "--aot":
            isAoTCompiled = true;
            break;
        case "--wasi-sdk":
            wasiSdkDir = args[++i];
            break;
        case "--wamr":
            wamrDir = args[++i];
            break;
        default:
            PrintUsage();
            break;
    }
}

// ---
// Configuration of WASI SDK.
// ---
string wasiSysrootDir = Path.Combine(wasiSdkDir, "share/wasi-sysroot");
string wasiDefinedSymbolsFile = Path.Combine(wasiSdkDir, "share/wasi-sysroot/share/wasm32-wasi/defined-symbols.txt");
string compilerPath = Path.Combine(wasiSdkDir, "bin/clang");

// ---
// Configuration of WAMR.
// ---
string wamrCompiler = Path.Combine(wamrDir, "wamr-compiler/build/wamrc");

// ---
// Internal variables.
// ---
var compiledWasmList = new List<FileInfo>();
var polyBenchUtilitiesDir = Path.Combine(polyBenchDir.FullName, "utilities");

// ---
// Compilation logic.
// ---
void CompileRecursively(DirectoryInfo directory)
{
    foreach (var file in directory.EnumerateFiles())
    {
        if (file.Name.EndsWith(".c"))
        {
            Compile(file);
        }
    }
    
    foreach (var subDirectory in directory.GetDirectories())
    {
        CompileRecursively(subDirectory);
    }
}

void Compile(FileInfo sourceFile)
{
    var wasmFile = string.IsNullOrEmpty(compilerOutputPath)
        ? new FileInfo(sourceFile.FullName.Replace(".c", ".wasm"))
        : new FileInfo(Path.Combine(compilerOutputPath, sourceFile.Name.Replace(".c", ".wasm")));
    
    Console.Write($"compiling {sourceFile.Name}.. ");
    
    var p = Process.Start(compilerPath, new[]
    {
        sourceFile.FullName,
        Path.Combine(polyBenchUtilitiesDir, "polybench.c"),
        "--target=wasm32-wasi",
        "-O3",
        $"--sysroot={wasiSysrootDir}",
        $"-Wl,--allow-undefined-file={wasiDefinedSymbolsFile}",
        "-Wl,--no-threads,--strip-all",
        "-Wl,--export=main",
        "-Wl,--export=benchmark",
        "-Wl,--export=finalize",
        $"-I{sourceFile.DirectoryName}",
        $"-I{polyBenchUtilitiesDir}",
        $"-o{wasmFile.FullName}"
    });

    p.Start();
    p.WaitForExit(); 
    
    if (p.ExitCode == 0)
    {
        Console.WriteLine("ok");
    }
    else
    {
        Environment.Exit(-1);
    }
    
    compiledWasmList.Add(wasmFile);
}

void CompileAheadOfTime(FileInfo wasmFile)
{
    var aotPath = wasmFile.FullName.Replace(".wasm", ".aot");
    var arguments = $"{(isCompiledForSgx ? "-sgx " : "")} -o {aotPath} {wasmFile.FullName}";
    var p = Process.Start(wamrCompiler, arguments);

    p!.Start();
    p.WaitForExit();

    if (p.ExitCode != 0)
    {
        Environment.Exit(-1);
    }
}

// ---
// Application flow.
// ---

// Compile each benchmark to WASM sequentially, in order to highlight any programming error(s)
foreach (var benchmarkFolder in benchmarkFolders)
{
    var directory = new DirectoryInfo(Path.Combine(polyBenchDir.FullName, benchmarkFolder));
    CompileRecursively(directory);
}

// Compile WASM to AoT in parallel if AoT compilation is requested
if (isAoTCompiled)
{
    if (!File.Exists(wamrCompiler))
    {
        Console.WriteLine($"error: the AoT compiler does not exist at: {wamrCompiler}");
        Environment.Exit(-1);
    }
    
    Parallel.ForEach(compiledWasmList, CompileAheadOfTime);
}

return 0;