using System.Text;
using System.Text.RegularExpressions;
using Com.Dowjones.Ucpp.DynamoDb;
using FilesAutomationTest.Models;
using Grpc.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TableParseFeedTest.Comparison;
using TableParseFeedTest.Native;
using TableParseFeedTest.Pipeline;

namespace TableParseFeedTest;

public static class Program
{
    private const string Usage = """
        Runs a feed file through normalizer -> enricher -> tableparse -> story producer in-process, with a
        locally built libTableParser.so, and compares the NDF story with the regression fixture exactly the
        way ucpp-files-test-automation does (routing, xml/ndf version, headline, codes, pbody).

        Usage: run.sh [options]

          --input <file>          feed file            (default: the BWF 20261108962633r1 regression input)
          --expected <file>       expected UCPP story  (default: its int regression output)
          --lib <file>            libTableParser.so    (default: newest ucpp-table-parse-lib/build*/LIBRARY/TABLE_PARSER/libTableParser.so)
          --config-path <dir>     PARSER_CONFIG_PATH, holds utf8-character-mappings.<UTFEXT>
                                  (default: ucpp-table-parse-lib/.regress_env/config-gbl)
          --database-path <dir>   replaces /nss/rtns/v_running/database/ for the query sets
                                  (default: ucpp-table-parse-lib/.regress_env/database)
          --manifest <file>       UCPP manifest        (default: Fixtures/int/bwf_manifest_1.0.json)
          --story <file>          story definition     (default: Fixtures/int/bwf_story.json)
          --lookups <file>        offline tables for the Redis-backed 'lookup'/'lookup-exists' operators
                                  (default: Fixtures/lookups.json, shape {"exchange": {"NYSE": {"ilxsuffix": ""}}})
          --provider-symbol <s>   (default: bwu)
          --provider-id <s>       (default: bwf_sftppush)
          --transaction-id <s>    (default: F/UCPPID of the expected story, so the generated E/ and F/UCPPID codes line up)
          --out <dir>             artifacts            (default: out/<input file name>)
          --verbose               echo service logs to the console
        """;

    public static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        // ucpp-tableparse's HtmlParser reads utf8-character-mappings from the working directory.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        Directory.CreateDirectory(options.OutputDirectory);
        using var log = new StreamWriter(Path.Combine(options.OutputDirectory, "services.log"));
        LogProxy.LogFile = log;
        LogProxy.Verbose = options.Verbose;

        Console.WriteLine($"input      {options.Input}");
        Console.WriteLine($"expected   {options.Expected}");
        Console.WriteLine($"library    {options.Library}  ({File.GetLastWriteTime(options.Library):yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine($"config     {options.ConfigPath}");
        Console.WriteLine($"database   {options.DatabasePath}");
        Console.WriteLine($"manifest   {options.Manifest}");
        Console.WriteLine($"story      {options.Story}");
        Console.WriteLine($"lookups    {options.Lookups}");
        Console.WriteLine();

        TableParserLibrary.Load(options.Library, options.ConfigPath, options.DatabasePath);

        var manifest = JsonConvert.DeserializeObject<UcppManifest>(await File.ReadAllTextAsync(options.Manifest))
            ?? throw new InvalidOperationException($"Cannot read manifest {options.Manifest}");
        var storyRules = (JObject.Parse(await File.ReadAllTextAsync(options.Story))["rules"] as JArray)?.Cast<object>().ToList()
            ?? throw new InvalidOperationException($"No 'rules' in story definition {options.Story}");
        var lookups = JObject.Parse(await File.ReadAllTextAsync(options.Lookups)).Properties()
            .ToDictionary(p => p.Name, p => (JObject)p.Value);

        var expectedStory = await File.ReadAllTextAsync(options.Expected);
        var expectedUcppId = Regex.Match(expectedStory, @"F/UCPPID=(?:""|&quot;)([0-9a-fA-F-]{36})").Groups[1].Value;
        var transactionId = options.TransactionId
            ?? (expectedUcppId.Length > 0 ? expectedUcppId : Guid.NewGuid().ToString());
        var headers = CreateHeaders(options, transactionId, manifest.Id);

        var pipeline = new FeedPipeline(manifest, storyRules, lookups, options.DatabasePath);
        PipelineResult result;
        try
        {
            result = await pipeline.RunAsync(await File.ReadAllBytesAsync(options.Input), headers);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"PIPELINE FAILED: {ex.Message}");
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                Console.WriteLine($"  caused by: {inner.Message}");
            }
            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "error.txt"), ex.ToString());
            return 3;
        }

        Artifacts.Write(options.OutputDirectory, result);
        foreach (var note in result.Notes.Distinct())
        {
            Console.WriteLine($"  note: {note}");
        }

        var failedTableParse = result.TableParseCalls.Where(c => c.Response.ContainsKey("error")).ToList();
        if (result.Stories.Count != 1)
        {
            Console.WriteLine();
            Console.WriteLine($"FAIL: story rules produced {result.Stories.Count} stories, the regression fixture holds 1.");
            return 1;
        }

        var actualStory = result.Stories[0].StoryContent;
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "actual-story.txt"), actualStory);

        var provider = new Provider { ProviderSymbol = options.ProviderSymbol, ProviderId = options.ProviderId };
        var components = StoryComparison.Compare(actualStory, expectedStory, provider);
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "expected-pbody.xml"), components.Single(c => c.Component == "body").Expected);
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "actual-pbody.xml"), components.Single(c => c.Component == "body").Actual);

        return Report.Print(components, failedTableParse, options.OutputDirectory);
    }

    private static Metadata CreateHeaders(Options options, string transactionId, string manifestId)
    {
        var fileName = Path.GetFileName(options.Input);
        return new Metadata
        {
            { "x-dj-transaction-id", transactionId },
            { "x-dj-ucpp-transaction-id", transactionId },
            { "x-dj-outer-transaction-id", transactionId },
            { "x-dj-file-name", fileName },
            { "x-dj-ucpp-file-name", fileName },
            { "x-dj-provider-symbol", options.ProviderSymbol },
            { "x-dj-ucpp-provider-symbol", options.ProviderSymbol },
            { "x-dj-ucpp-provider-id", options.ProviderId },
            { "x-dj-ucpp-manifest-id", manifestId },
        };
    }
}

public class Options
{
    public bool Help { get; private set; }
    public bool Verbose { get; private set; }
    public string Input { get; private set; } = string.Empty;
    public string Expected { get; private set; } = string.Empty;
    public string Library { get; private set; } = string.Empty;
    public string ConfigPath { get; private set; } = string.Empty;
    public string DatabasePath { get; private set; } = string.Empty;
    public string Manifest { get; private set; } = string.Empty;
    public string Story { get; private set; } = string.Empty;
    public string Lookups { get; private set; } = string.Empty;
    public string ProviderSymbol { get; private set; } = "bwu";
    public string ProviderId { get; private set; } = "bwf_sftppush";
    public string? TransactionId { get; private set; }
    public string OutputDirectory { get; private set; } = string.Empty;

    public static Options Parse(string[] args)
    {
        var workspace = FindWorkspace();
        var files = Path.Combine(workspace, "ucpp-files-test-automation", "FilesAutomationTest", "Files");
        var lib = Path.Combine(workspace, "ucpp-table-parse-lib");
        var tool = Path.Combine(workspace, "ucpp-tableparse-feed-test");

        var o = new Options
        {
            Input = Path.Combine(files, "inputs", "bwu", "bwf_sftppush", "20261108962633r1-Tweak-to-BWF_1.xml"),
            Expected = Path.Combine(files, "int", "bwu", "bwf_sftppush", "outputs", "ucpp", "20261108962633r1-Tweak-to-BWF_134214003443.txt"),
            ConfigPath = Path.Combine(lib, ".regress_env", "config-gbl"),
            DatabasePath = Path.Combine(lib, ".regress_env", "database"),
            Manifest = Path.Combine(tool, "Fixtures", "int", "bwf_manifest_1.0.json"),
            Story = Path.Combine(tool, "Fixtures", "int", "bwf_story.json"),
            Lookups = Path.Combine(tool, "Fixtures", "lookups.json"),
        };
        string? library = null, output = null;

        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? Path.GetFullPath(args[++i]) : throw new ArgumentException($"{args[i]} needs a value");
            string Text() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "-h" or "--help": o.Help = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--input": o.Input = Value(); break;
                case "--expected": o.Expected = Value(); break;
                case "--lib": library = Value(); break;
                case "--config-path": o.ConfigPath = Value(); break;
                case "--database-path": o.DatabasePath = Value(); break;
                case "--manifest": o.Manifest = Value(); break;
                case "--story": o.Story = Value(); break;
                case "--lookups": o.Lookups = Value(); break;
                case "--provider-symbol": o.ProviderSymbol = Text(); break;
                case "--provider-id": o.ProviderId = Text(); break;
                case "--transaction-id": o.TransactionId = Text(); break;
                case "--out": output = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        o.Library = library ?? NewestLibrary(lib);
        o.OutputDirectory = output ?? Path.Combine(tool, "out", Path.GetFileNameWithoutExtension(o.Input));

        foreach (var (name, path) in new[] { ("--input", o.Input), ("--expected", o.Expected), ("--lib", o.Library), ("--manifest", o.Manifest), ("--story", o.Story), ("--lookups", o.Lookups) })
        {
            if (!File.Exists(path)) throw new ArgumentException($"{name}: file not found: {path}");
        }
        foreach (var (name, path) in new[] { ("--config-path", o.ConfigPath), ("--database-path", o.DatabasePath) })
        {
            if (!Directory.Exists(path)) throw new ArgumentException($"{name}: directory not found: {path}");
        }

        return o;
    }

    private static string NewestLibrary(string libRepo)
    {
        var candidates = Directory.Exists(libRepo)
            ? Directory.GetDirectories(libRepo, "build*")
                .Select(d => Path.Combine(d, "LIBRARY", "TABLE_PARSER", "libTableParser.so"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList()
            : new List<string>();

        return candidates.FirstOrDefault()
            ?? throw new ArgumentException($"--lib: no build*/LIBRARY/TABLE_PARSER/libTableParser.so under {libRepo}; build it or pass --lib");
    }

    private static string FindWorkspace()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ucpp-files-test-automation")) &&
                Directory.Exists(Path.Combine(dir.FullName, "ucpp-table-parse-lib")))
            {
                return dir.FullName;
            }
        }
        throw new ArgumentException("Cannot find the workspace (the directory holding ucpp-files-test-automation and ucpp-table-parse-lib)");
    }
}
