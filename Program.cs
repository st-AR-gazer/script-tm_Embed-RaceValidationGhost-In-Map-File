using System.Diagnostics;
using System.Reflection;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Exceptions;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            if (!CliOptions.TryParse(args, out var options, out var parseError))
            {
                Console.WriteLine(parseError);
                Console.WriteLine();
                PrintUsage();
                return 1;
            }

            if (!File.Exists(options.MapPath))
            {
                Console.WriteLine($"Map file not found: {options.MapPath}");
                return 2;
            }

            if (!File.Exists(options.GhostPath))
            {
                Console.WriteLine($"Ghost file not found: {options.GhostPath}");
                return 2;
            }

            if (!TryResolveGbxlzo(options, out var gbxlzoPath, out var gbxlzoError))
            {
                Console.WriteLine(gbxlzoError);
                return 3;
            }

            var outputPath = options.OutputPath ?? BuildDefaultOutputPath(options.MapPath);
            EnsureParentDirectory(outputPath);

            Console.WriteLine($"Map:     {Path.GetFullPath(options.MapPath)}");
            Console.WriteLine($"Ghost:   {Path.GetFullPath(options.GhostPath)}");
            Console.WriteLine($"Output:  {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"gbxlzo:  {gbxlzoPath}");
            Console.WriteLine();

            using var temp = TemporaryWorkspace.Create(options.TempRoot, options.KeepTemp);
            var mapUncompressedPath = temp.GetFilePath("map.uncompressed.gbx");
            var ghostUncompressedPath = temp.GetFilePath("ghost.uncompressed.gbx");
            var outputUncompressedPath = temp.GetFilePath("output.uncompressed.gbx");

            SafeDecompress(gbxlzoPath, options.MapPath, mapUncompressedPath, "map");
            SafeDecompress(gbxlzoPath, options.GhostPath, ghostUncompressedPath, "ghost");

            EmbedValidationGhost(mapUncompressedPath, ghostUncompressedPath, outputUncompressedPath);

            Console.WriteLine("Compressing output map...");
            RunGbxlzoOrThrow(gbxlzoPath, outputUncompressedPath, outputPath, compress: true);

            Console.WriteLine();
            Console.WriteLine($"Done. Embedded map written to: {Path.GetFullPath(outputPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed:");
            Console.WriteLine(ex.Message);
            return 99;
        }
    }

    private static void SafeDecompress(string gbxlzoPath, string inputPath, string outputPath, string label)
    {
        Console.WriteLine($"Decompressing {label}...");
        var result = RunGbxlzo(gbxlzoPath, inputPath, outputPath, compress: false);
        if (result == 0)
        {
            return;
        }

        Console.WriteLine($"Decompress failed for {label} (exit {result}). Trying direct copy fallback...");
        File.Copy(inputPath, outputPath, overwrite: true);
    }

    private static void EmbedValidationGhost(string mapPath, string ghostPath, string outPath)
    {
        Console.WriteLine("Embedding RaceValidateGhost...");

        Gbx<CGameCtnChallenge> mapGbx;
        CGameCtnGhost ghostNode;

        try
        {
            mapGbx = Gbx.Parse<CGameCtnChallenge>(mapPath);
        }
        catch (LzoNotDefinedException)
        {
            throw new InvalidOperationException("Map still appears compressed after decompression step.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to parse map: {mapPath}. {ex.Message}");
        }

        try
        {
            ghostNode = Gbx.ParseNode<CGameCtnGhost>(ghostPath);
        }
        catch (LzoNotDefinedException)
        {
            throw new InvalidOperationException("Ghost still appears compressed after decompression step.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to parse ghost: {ghostPath}. {ex.Message}");
        }

        var mapNode = mapGbx.Node;
        mapNode.ChallengeParameters ??= new CGameCtnChallengeParameters();
        mapNode.ChallengeParameters.RaceValidateGhost = ghostNode;

        var writeSettings = new GbxWriteSettings();
        ForceUncompressed(writeSettings);
        mapGbx.Save(outPath, writeSettings);
    }

    private static bool TryResolveGbxlzo(CliOptions options, out string gbxlzoPath, out string error)
    {
        if (!string.IsNullOrWhiteSpace(options.GbxlzoPath))
        {
            var explicitPath = Path.GetFullPath(options.GbxlzoPath);
            if (File.Exists(explicitPath))
            {
                gbxlzoPath = explicitPath;
                error = string.Empty;
                return true;
            }

            gbxlzoPath = string.Empty;
            error = $"gbxlzo not found at --gbxlzo path: {explicitPath}";
            return false;
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "gbxlzo.exe"),
            Path.Combine(AppContext.BaseDirectory, "gbxlzo.exe")
        };

        var mapDir = Path.GetDirectoryName(Path.GetFullPath(options.MapPath));
        if (!string.IsNullOrWhiteSpace(mapDir))
        {
            candidates.Add(Path.Combine(mapDir, "gbxlzo.exe"));
        }

        var ghostDir = Path.GetDirectoryName(Path.GetFullPath(options.GhostPath));
        if (!string.IsNullOrWhiteSpace(ghostDir))
        {
            candidates.Add(Path.Combine(ghostDir, "gbxlzo.exe"));
        }

        foreach (var pathDir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(pathDir, "gbxlzo.exe"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                gbxlzoPath = Path.GetFullPath(candidate);
                error = string.Empty;
                return true;
            }
        }

        gbxlzoPath = string.Empty;
        error = "Could not find gbxlzo.exe. Put it in this folder, add it to PATH, or pass --gbxlzo <path>.";
        return false;
    }

    private static int RunGbxlzo(string gbxlzoPath, string inputPath, string outputPath, bool compress)
    {
        using var process = new Process();
        process.StartInfo.FileName = gbxlzoPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add(inputPath);
        process.StartInfo.ArgumentList.Add(compress ? "-c" : "-d");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(outputPath);

        process.Start();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void RunGbxlzoOrThrow(string gbxlzoPath, string inputPath, string outputPath, bool compress)
    {
        var code = RunGbxlzo(gbxlzoPath, inputPath, outputPath, compress);
        if (code != 0)
        {
            throw new InvalidOperationException($"gbxlzo failed with exit code {code} while {(compress ? "compressing" : "decompressing")} {inputPath}");
        }
    }

    private static string BuildDefaultOutputPath(string mapPath)
    {
        var mapDir = Path.GetDirectoryName(mapPath) ?? ".";
        var ext = Path.GetExtension(mapPath);
        var stem = Path.GetFileNameWithoutExtension(mapPath);

        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".Gbx";
        }

        if (stem.EndsWith(".Map", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return Path.Combine(mapDir, $"{stem}_withValidationGhost.Map{ext}");
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine(@"  dotnet run -- ""<Map.Gbx>"" ""<Ghost.Gbx>"" [Out.Map.Gbx]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine(@"  --out <path>       Output map path (same as optional positional Out.Map.Gbx).");
        Console.WriteLine(@"  --gbxlzo <path>    Path to gbxlzo.exe (if not in current folder/PATH).");
        Console.WriteLine(@"  --temp <folder>    Custom temporary working root.");
        Console.WriteLine(@"  --keep-temp        Keep temporary files for inspection.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine(@"  dotnet run -- ""C:\maps\track.Map.Gbx"" ""C:\maps\run.Ghost.Gbx""");
        Console.WriteLine(@"  dotnet run -- ""track.Map.Gbx"" ""run.Ghost.Gbx"" ""track_withghost.Map.Gbx""");
        Console.WriteLine(@"  dotnet run -- ""track.Map.Gbx"" ""run.Ghost.Gbx"" --gbxlzo ""C:\tools\gbxlzo.exe""");
    }

    private static void ForceUncompressed(object settings)
    {
        var type = settings.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var propertyType = property.PropertyType;
            if (propertyType == typeof(GbxCompression))
            {
                property.SetValue(settings, GbxCompression.Uncompressed);
            }
            else if (Nullable.GetUnderlyingType(propertyType) == typeof(GbxCompression))
            {
                property.SetValue(settings, (GbxCompression?)GbxCompression.Uncompressed);
            }
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly bool keep;

        public string DirectoryPath { get; }

        private TemporaryWorkspace(string directoryPath, bool keep)
        {
            DirectoryPath = directoryPath;
            this.keep = keep;
        }

        public static TemporaryWorkspace Create(string? tempRoot, bool keep)
        {
            var root = string.IsNullOrWhiteSpace(tempRoot) ? Path.GetTempPath() : tempRoot;
            var name = $"tm-ghost-embed-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            Console.WriteLine($"Temp:    {Path.GetFullPath(dir)}");
            return new TemporaryWorkspace(dir, keep);
        }

        public string GetFilePath(string fileName)
        {
            return System.IO.Path.Combine(DirectoryPath, fileName);
        }

        public void Dispose()
        {
            if (keep)
            {
                Console.WriteLine($"Kept temp files at: {System.IO.Path.GetFullPath(DirectoryPath)}");
                return;
            }

            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to delete temp folder {DirectoryPath}: {ex.Message}");
            }
        }
    }

    private sealed class CliOptions
    {
        public required string GhostPath { get; init; }
        public required string MapPath { get; init; }
        public string? OutputPath { get; init; }
        public string? GbxlzoPath { get; init; }
        public string? TempRoot { get; init; }
        public bool KeepTemp { get; init; }

        public static bool TryParse(string[] args, out CliOptions options, out string error)
        {
            var positional = new List<string>();
            string? outputPath = null;
            string? gbxlzoPath = null;
            string? tempRoot = null;
            var keepTemp = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--out":
                    case "-o":
                        if (!TryReadValue(args, ref i, out outputPath, out error))
                        {
                            options = null!;
                            return false;
                        }
                        break;
                    case "--gbxlzo":
                        if (!TryReadValue(args, ref i, out gbxlzoPath, out error))
                        {
                            options = null!;
                            return false;
                        }
                        break;
                    case "--temp":
                        if (!TryReadValue(args, ref i, out tempRoot, out error))
                        {
                            options = null!;
                            return false;
                        }
                        break;
                    case "--keep-temp":
                        keepTemp = true;
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                        {
                            options = null!;
                            error = $"Unknown option: {arg}";
                            return false;
                        }

                        positional.Add(arg);
                        break;
                }
            }

            if (positional.Count < 2 || positional.Count > 3)
            {
                options = null!;
                error = "Expected 2 or 3 positional arguments: <Map.Gbx> <Ghost.Gbx> [Out.Map.Gbx]";
                return false;
            }

            if (positional.Count == 3)
            {
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    options = null!;
                    error = "Output path provided twice (positional and --out). Use only one.";
                    return false;
                }

                outputPath = positional[2];
            }

            options = new CliOptions
            {
                MapPath = positional[0],
                GhostPath = positional[1],
                OutputPath = outputPath,
                GbxlzoPath = gbxlzoPath,
                TempRoot = tempRoot,
                KeepTemp = keepTemp
            };
            error = string.Empty;
            return true;
        }

        private static bool TryReadValue(string[] args, ref int index, out string? value, out string error)
        {
            if (index + 1 >= args.Length)
            {
                value = null;
                error = $"Missing value for option: {args[index]}";
                return false;
            }

            index++;
            value = args[index];
            error = string.Empty;
            return true;
        }
    }
}
