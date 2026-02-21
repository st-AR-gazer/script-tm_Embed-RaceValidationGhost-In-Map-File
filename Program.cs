using System.Diagnostics;
using System.Reflection;
using System.Collections.Immutable;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.Script;
using GBX.NET.Exceptions;

internal static class Program
{
    private const string DefaultSignatureMetadataKey = "EmbedRaceValidationGhost";
    private const string ToolName = "EmbedRaceValidationGhost";
    private const string SignatureText = "Validation ghost embedded by ar's EmbedRaceValidationGhost tool";

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

            if (!File.Exists(options.GhostOrReplayPath))
            {
                Console.WriteLine($"Ghost/replay file not found: {options.GhostOrReplayPath}");
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
            Console.WriteLine($"Input:   {Path.GetFullPath(options.GhostOrReplayPath)}");
            Console.WriteLine($"Index:   {options.GhostIndex}");
            Console.WriteLine($"Output:  {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"gbxlzo:  {gbxlzoPath}");
            Console.WriteLine();

            using var temp = TemporaryWorkspace.Create(options.TempRoot, options.KeepTemp);
            var mapUncompressedPath = temp.GetFilePath("map.uncompressed.gbx");
            var ghostOrReplayUncompressedPath = temp.GetFilePath("ghost_or_replay.uncompressed.gbx");
            var outputUncompressedPath = temp.GetFilePath("output.uncompressed.gbx");

            SafeDecompress(gbxlzoPath, options.MapPath, mapUncompressedPath, "map");
            SafeDecompress(gbxlzoPath, options.GhostOrReplayPath, ghostOrReplayUncompressedPath, "ghost/replay");

            EmbedValidationGhost(
                mapUncompressedPath,
                ghostOrReplayUncompressedPath,
                outputUncompressedPath,
                options.GhostIndex,
                options.MapPath,
                options.GhostOrReplayPath);

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

    private static void EmbedValidationGhost(
        string mapPath,
        string ghostOrReplayPath,
        string outPath,
        int ghostIndex,
        string originalMapPath,
        string originalGhostOrReplayPath)
    {
        Console.WriteLine("Embedding RaceValidateGhost...");

        Gbx<CGameCtnChallenge> mapGbx;
        GhostSelectionResult ghostSelection;

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
            ghostSelection = ParseGhostFromGhostOrReplay(ghostOrReplayPath, ghostIndex);
        }
        catch (LzoNotDefinedException)
        {
            throw new InvalidOperationException("Ghost/replay input still appears compressed after decompression step.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to parse ghost/replay input: {ghostOrReplayPath}. {ex.Message}");
        }

        var mapNode = mapGbx.Node;
        var hadExistingValidationGhost = mapNode.ChallengeParameters?.RaceValidateGhost is not null;
        mapNode.ChallengeParameters ??= new CGameCtnChallengeParameters();
        mapNode.ChallengeParameters.RaceValidateGhost = ghostSelection.Ghost;

        AddEmbeddingMetadata(
            mapNode,
            ghostSelection,
            originalMapPath,
            originalGhostOrReplayPath,
            hadExistingValidationGhost);

        var writeSettings = new GbxWriteSettings();
        ForceUncompressed(writeSettings);
        mapGbx.Save(outPath, writeSettings);
    }

    private static GhostSelectionResult ParseGhostFromGhostOrReplay(string path, int ghostIndex)
    {
        var node = Gbx.ParseNode(path);

        return node switch
        {
            CGameCtnGhost ghost => ExtractGhostFromSingleGhost(ghost, ghostIndex),
            CGameCtnReplayRecord replay => ExtractGhostFromReplay(replay, ghostIndex),
            CGameCtnMediaClip clip => ExtractGhostFromClip(clip, ghostIndex),
            null => throw new InvalidOperationException("Input file has no readable node."),
            _ => throw new InvalidOperationException($"Input type {node.GetType().Name} is unsupported. Provide a .Ghost.Gbx or .Replay.Gbx file.")
        };
    }

    private static GhostSelectionResult ExtractGhostFromSingleGhost(CGameCtnGhost ghost, int ghostIndex)
    {
        if (ghostIndex != 0)
        {
            Console.WriteLine($"Input has a single ghost. Ignoring --ghost-index {ghostIndex} and using that ghost.");
        }

        return new GhostSelectionResult(
            Ghost: ghost,
            SourceKind: "Ghost",
            RequestedGhostIndex: ghostIndex,
            UsedGhostIndex: 0,
            AvailableGhostCount: 1);
    }

    private static GhostSelectionResult ExtractGhostFromReplay(CGameCtnReplayRecord replay, int ghostIndex)
    {
        ImmutableList<CGameCtnGhost> ghosts = replay.GetGhosts().ToImmutableList();
        return ExtractGhostFromList(ghosts, ghostIndex, "Replay");
    }

    private static GhostSelectionResult ExtractGhostFromClip(CGameCtnMediaClip clip, int ghostIndex)
    {
        ImmutableList<CGameCtnGhost> ghosts = clip.GetGhosts().ToImmutableList();
        return ExtractGhostFromList(ghosts, ghostIndex, "Clip");
    }

    private static GhostSelectionResult ExtractGhostFromList(ImmutableList<CGameCtnGhost> ghosts, int ghostIndex, string sourceKind)
    {
        if (ghosts.Count == 0)
        {
            throw new InvalidOperationException($"{sourceKind} file does not contain any ghosts.");
        }

        if (ghosts.Count == 1)
        {
            if (ghostIndex != 0)
            {
                Console.WriteLine($"{sourceKind} contains a single ghost. Ignoring --ghost-index {ghostIndex} and using index 0.");
            }

            return new GhostSelectionResult(
                Ghost: ghosts[0],
                SourceKind: sourceKind,
                RequestedGhostIndex: ghostIndex,
                UsedGhostIndex: 0,
                AvailableGhostCount: 1);
        }

        if (ghostIndex >= ghosts.Count)
        {
            throw new InvalidOperationException($"{sourceKind} ghost index {ghostIndex} is out of range. Available range: 0..{ghosts.Count - 1}.");
        }

        if (ghosts.Count > 1)
        {
            Console.WriteLine($"{sourceKind} contains {ghosts.Count} ghosts. Using index {ghostIndex}.");
        }

        return new GhostSelectionResult(
            Ghost: ghosts[ghostIndex],
            SourceKind: sourceKind,
            RequestedGhostIndex: ghostIndex,
            UsedGhostIndex: ghostIndex,
            AvailableGhostCount: ghosts.Count);
    }

    private static void AddEmbeddingMetadata(
        CGameCtnChallenge map,
        GhostSelectionResult selection,
        string originalMapPath,
        string originalGhostOrReplayPath,
        bool replacedExistingValidationGhost)
    {
        var metadata = map.ScriptMetadata ??= new CScriptTraitsMetadata();
        EnsureMetadataChunk(metadata);
        metadata.Traits ??= new Dictionary<string, CScriptTraitsMetadata.ScriptTrait>();

        var key = ResolveSignatureMetadataKey();
        var ghost = selection.Ghost;

        var ghostBuilder = new CScriptTraitsMetadata.ScriptStructTraitBuilder("SelectedGhost")
            .WithText("GhostUid", ghost.GhostUid?.ToString() ?? string.Empty)
            .WithText("Validate_RaceSettings", ghost.Validate_RaceSettings ?? string.Empty)
            .WithText("Validate_ExeVersion", ghost.Validate_ExeVersion ?? string.Empty)
            .WithText("RaceTime", ghost.RaceTime?.ToString() ?? string.Empty);

        var signatureBuilder = new CScriptTraitsMetadata.ScriptStructTraitBuilder(key)
            .WithText("Signature", SignatureText)
            .WithText("ToolName", ToolName)
            .WithText("Action", "EmbedRaceValidateGhost")
            .WithText("EmbeddedAtUtc", DateTime.UtcNow.ToString("O"))
            .WithText("MapFileName", Path.GetFileName(originalMapPath) ?? string.Empty)
            .WithText("GhostOrReplayFileName", Path.GetFileName(originalGhostOrReplayPath) ?? string.Empty)
            .WithText("InputType", selection.SourceKind)
            .WithInteger("RequestedGhostIndex", selection.RequestedGhostIndex)
            .WithInteger("UsedGhostIndex", selection.UsedGhostIndex)
            .WithInteger("GhostCountInInput", selection.AvailableGhostCount)
            .WithInteger("ReplacedExistingValidationGhost", replacedExistingValidationGhost ? 1 : 0)
            .WithStruct("RaceValidateGhost", ghostBuilder);

        metadata.Remove(key);
        metadata.Declare(key, signatureBuilder.Build());

        Console.WriteLine($"Embedded signature metadata under ScriptMetadata key '{key}'.");
    }

    private static string ResolveSignatureMetadataKey()
    {
        var env = Environment.GetEnvironmentVariable("TM_EMBED_META_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var legacyEnv = Environment.GetEnvironmentVariable("TM_META_KEY");
        if (!string.IsNullOrWhiteSpace(legacyEnv))
        {
            return legacyEnv.Trim();
        }

        return DefaultSignatureMetadataKey;
    }

    private static void EnsureMetadataChunk(CScriptTraitsMetadata metadata)
    {
        if (metadata.Chunks is null)
        {
            return;
        }

        if (metadata.Chunks.Count == 0)
        {
            metadata.CreateChunk<CScriptTraitsMetadata.Chunk11002000>();
        }
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

        var ghostDir = Path.GetDirectoryName(Path.GetFullPath(options.GhostOrReplayPath));
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
        Console.WriteLine(@"  dotnet run -- ""<Map.Gbx>"" ""<GhostOrReplay.Gbx>"" [Out.Map.Gbx]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine(@"  --out <path>       Output map path (same as optional positional Out.Map.Gbx).");
        Console.WriteLine(@"  --gbxlzo <path>    Path to gbxlzo.exe (if not in current folder/PATH).");
        Console.WriteLine(@"  --ghost-index <n>  Ghost index for replay/clip inputs (default: 0).");
        Console.WriteLine(@"  --temp <folder>    Custom temporary working root.");
        Console.WriteLine(@"  --keep-temp        Keep temporary files for inspection.");
        Console.WriteLine();
        Console.WriteLine("Metadata:");
        Console.WriteLine(@"  - Writes signature metadata to ScriptMetadata key 'EmbedRaceValidationGhost'.");
        Console.WriteLine(@"  - Set TM_EMBED_META_KEY (or TM_META_KEY) to override that key.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine(@"  dotnet run -- ""C:\maps\track.Map.Gbx"" ""C:\maps\run.Ghost.Gbx""");
        Console.WriteLine(@"  dotnet run -- ""C:\maps\track.Map.Gbx"" ""C:\maps\run.Replay.Gbx""");
        Console.WriteLine(@"  dotnet run -- ""C:\maps\track.Map.Gbx"" ""C:\maps\pack.Replay.Gbx"" --ghost-index 2");
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

    private sealed record GhostSelectionResult(
        CGameCtnGhost Ghost,
        string SourceKind,
        int RequestedGhostIndex,
        int UsedGhostIndex,
        int AvailableGhostCount);

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
        public required string GhostOrReplayPath { get; init; }
        public required string MapPath { get; init; }
        public string? OutputPath { get; init; }
        public string? GbxlzoPath { get; init; }
        public string? TempRoot { get; init; }
        public int GhostIndex { get; init; }
        public bool KeepTemp { get; init; }

        public static bool TryParse(string[] args, out CliOptions options, out string error)
        {
            var positional = new List<string>();
            string? outputPath = null;
            string? gbxlzoPath = null;
            string? tempRoot = null;
            var ghostIndex = 0;
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
                    case "--ghost-index":
                        if (!TryReadValue(args, ref i, out var ghostIndexRaw, out error))
                        {
                            options = null!;
                            return false;
                        }

                        if (!int.TryParse(ghostIndexRaw, out ghostIndex) || ghostIndex < 0)
                        {
                            options = null!;
                            error = $"Invalid value for --ghost-index: {ghostIndexRaw}. Use a non-negative integer.";
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
                error = "Expected 2 or 3 positional arguments: <Map.Gbx> <GhostOrReplay.Gbx> [Out.Map.Gbx]";
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
                GhostOrReplayPath = positional[1],
                OutputPath = outputPath,
                GbxlzoPath = gbxlzoPath,
                TempRoot = tempRoot,
                GhostIndex = ghostIndex,
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
