using Com.Dowjones.CopyFlow.Helper;
using Com.Dowjones.Ucpp.DynamoDb;
using Com.Dowjones.Ucpp.JsonLogic.Net;
using Com.Dowjones.Ucpp.JsonLogic.Net.UcppStoryProducerOperations;
using Com.Dowjones.Ucpp.Logging;
using Com.Dowjones.Ucpp.Mesh.Service.Services;
using Com.Dowjones.Ucpp.Payloads.Payloads.ManifestPayloads.Normalizers;
using Grpc.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Com.Dowjones.Ucpp.JsonLogic.Net.UcppStoryProducerOperations.StoryOperationConstants.JsonLogicInputKeys;
using TableParseUcppService = Com.Dowjones.Ucpp.TableParse.GRPC.UcppService;

namespace TableParseFeedTest.Pipeline;

public class PipelineResult
{
    public Dictionary<string, NormalizerValue> NormalizedData { get; set; } = new();
    public Dictionary<string, string> EnrichedData { get; set; } = new();
    public List<UcppStory> Stories { get; set; } = new();
    public List<TableParseCall> TableParseCalls { get; set; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Normalizer -> Enricher -> Story producer for one input file, with the real JsonLogic operation
/// managers and the real ucpp-enricher Processor. Only the transport between the hops is replaced.
/// </summary>
public class FeedPipeline
{
    private readonly UcppManifest _manifest;
    private readonly List<object> _storyRules;
    private readonly IReadOnlyDictionary<string, JObject> _lookupTables;
    private readonly string _databasePath;

    public FeedPipeline(UcppManifest manifest, List<object> storyRules, IReadOnlyDictionary<string, JObject> lookupTables, string databasePath)
    {
        _manifest = manifest;
        _storyRules = storyRules;
        _lookupTables = lookupTables;
        _databasePath = databasePath;
    }

    public async Task<PipelineResult> RunAsync(byte[] contents, Metadata headers)
    {
        var result = new PipelineResult();
        var logging = LogProxy.Create<ILogging<EvaluateOperators>>();
        var router = new InProcessRouter(new LocalParserTableParse(_databasePath), LogProxy.Create<IScopedLogging<TableParseUcppService>>());

        var normalizerRoute = FindRoute(_manifest.Routes, "normalizer")
            ?? throw new InvalidOperationException($"Manifest '{_manifest.Id}' has no Normalizer route");
        var enricherRoute = FindRoute(normalizerRoute.Routes, "enricher")
            ?? throw new InvalidOperationException($"Manifest '{_manifest.Id}' has no Enricher route under the Normalizer");

        // --- ucpp-normalizer: Services/NomralizerService.ExecuteJsonLogic
        Console.WriteLine("[normalizer] manifest normalization rules (normalize-xml -> in-process xmlprocessor)");
        var normalizerOperations = new NormalizerOperationManager(router, logging, null!, null!, null!);
        var normalizerData = new JObject
        {
            { "content", contents },
            { "extractionFields", JsonConvert.SerializeObject(normalizerRoute.Normalization.ExtractionFields) }
        };
        var (normalizerPayload, _) = await new JsonLogicEvaluator(normalizerOperations)
            .JsonLogicProcessorAsync(normalizerData, normalizerRoute.Normalization.Rules, headers);
        result.NormalizedData = normalizerPayload.NormalizedData.ToDictionary(x => x.Key, x => x.Value);
        Console.WriteLine($"             {result.NormalizedData.Count} extraction fields");

        // --- ucpp-enricher: GRPC/UcppService.PerformProcessing -> Services/Processor.Process
        Console.WriteLine("[enricher]   manifest enrichment rules (tableparse -> ucpp-tableparse HtmlParser -> wrapper -> libTableParser.so)");
        var enricherOperations = CreateEnricherOperations(router, logging);
        OfflineOperators.Register(enricherOperations, _lookupTables, result.Notes);
        var enrichment = await new Processor(new JsonLogicEvaluator(enricherOperations))
            .Process(result.NormalizedData, enricherRoute.Enrichment, headers);
        result.EnrichedData = new Dictionary<string, string>(enrichment.EnrichedData);
        result.TableParseCalls = router.TableParseCalls;
        Console.WriteLine($"             {router.TableParseCalls.Count} tableparse calls, {router.TableParseCalls.Count(c => c.Response.ContainsKey("error"))} failed");

        // --- ucpp-storyproducer: Services/PostEnrichmentProcessor.CreateData + Services/StoryEvaluator.Evaluate
        Console.WriteLine("[story]      story rules (parser-feedback-content -> NDF)");
        var storyData = CreateStoryData(result.EnrichedData, headers);
        var storyOperations = new StoryProducerOperationManager(null!, null!, new Sha256HashService(), logging, new CopyflowSequenceGenerator(), null!);
        result.Stories = (await new JsonLogicEvaluator(storyOperations).EvaluateAsync<UcppStory>(_storyRules, storyData, headers)).ToList();
        Console.WriteLine($"             {result.Stories.Count} stories");

        return result;
    }

    private static EnricherOperationManager CreateEnricherOperations(InProcessRouter router, ILogging<EvaluateOperators> logging)
        => new(
            router,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, // feed enrichers (ABM, ECB, SCOM, MOFPF, DGAP, DCE)
            null!, null!, null!, null!,                                     // html-to-text, lookup, ISIN, WSJ executors
            null!, null!,                                                   // Redis, module rules
            logging,
            null!, null!, null!, null!, null!, null!, null!,                // YOM/Giants, alerts, Slack, TDNet, DynamoDB
            null!, null!, null!, null!, null!,                              // INFG, comparison values, Korean earnings
            new Sha256HashService(),
            null!, null!, null!, null!, null!,                              // CAPI, HK earnings, HTML validator, S3, regional config
            new CopyflowSequenceGenerator(),
            null!);

    // ucpp-enricher UcppService builds a UcppDocument from these keys; the story producer then
    // folds it back into the data dictionary.
    private static Dictionary<string, string> CreateStoryData(Dictionary<string, string> enrichedData, Metadata headers)
    {
        var data = new Dictionary<string, string>(enrichedData);
        enrichedData.TryGetValue("headline", out var headline);
        enrichedData.TryGetValue("body", out var body);
        enrichedData.TryGetValue("codelines", out var codeLines);
        enrichedData.TryGetValue("routing", out var routing);

        if (!string.IsNullOrEmpty(headline)) data.SetStoryData(HEADLINE, headline);
        if (!string.IsNullOrEmpty(body)) data.SetStoryData(BODY, body);
        var codes = codeLines?.Split(" ").ToList() ?? new List<string>();
        if (codes.Any()) data.SetStoryData(CODE_LINES, string.Join(" ", codes));
        if (!string.IsNullOrEmpty(routing)) data.SetStoryData(ROUTING, routing);
        data.SetStoryData(PROVIDER, headers.GetValue("x-dj-ucpp-provider-symbol"));
        return data;
    }

    private static Route? FindRoute(IEnumerable<Route>? routes, string type)
        => routes?.FirstOrDefault(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase));
}
