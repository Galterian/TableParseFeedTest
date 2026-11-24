using Com.Dowjones.Pib.UCPP.Protos;
using Com.Dowjones.Ucpp.Logging;
using Com.Dowjones.Ucpp.Payloads.Payloads.BasePayloads;
using Com.Dowjones.Ucpp.Payloads.Payloads.BasicPayloads.HtmlToText;
using Com.Dowjones.Ucpp.Payloads.Payloads.BasicPayloads.XmlProcessor;
using Com.Dowjones.Ucpp.Routing;
using Com.Dowjones.Ucpp.Routing.Transactions;
using Com.Dowjones.Ucpp.TableParse.Diagnostics;
using Com.Dowjones.Ucpp.TableParse.Html;
using Grpc.Core;
using ProtoBuf;
using TableParseUcppService = Com.Dowjones.Ucpp.TableParse.GRPC.UcppService;

namespace TableParseFeedTest.Pipeline;

public record TableParseCall(TableParsePayload Payload, Dictionary<string, string> Response, IReadOnlyList<string> NativeCalls);

/// <summary>
/// Stands in for the gRPC router of the normalizer and the enricher. Instead of calling the
/// ucpp-xmlprocessor / ucpp-tableparse pods it answers in-process, with the same response shape
/// (<c>x-response</c> protobuf for XmlProcessorOutput, the raw <c>text</c>/<c>ada</c>/<c>x2e</c>
/// map for TableParse).
/// </summary>
public class InProcessRouter : IBasicRouter
{
    // ucpp-common CommonConstants: what every caller sees for a tableparse failure.
    private const string TableParseErrorCode = "1259000";
    private const string ResponseKey = "x-response";

    private readonly HtmlParser _htmlParser;
    private readonly LocalParserTableParse _parserTableParse;
    private readonly IScopedLogging<TableParseUcppService> _tableParseLogging;

    public InProcessRouter(LocalParserTableParse parserTableParse, IScopedLogging<TableParseUcppService> tableParseLogging)
    {
        _parserTableParse = parserTableParse;
        // One instance, like the IHtmlParser singleton in ucpp-tableparse Startup: its lock is per instance.
        _htmlParser = new HtmlParser(parserTableParse);
        _tableParseLogging = tableParseLogging;
    }

    public List<TableParseCall> TableParseCalls { get; } = new();

    public Task<RoutingResponse> Route(IBasicPayload outputPayload, Metadata metadata, int timeoutMs = int.MaxValue)
        => PipelineRoute(outputPayload, metadata, timeoutMs);

    public async Task<RoutingResponse> PipelineRoute(IBasicPayload outputPayload, Metadata metadata, int timeoutMs = int.MaxValue)
    {
        return outputPayload switch
        {
            XmlProcessorPayload xml => XmlProcessor(xml, metadata),
            TableParsePayload tableParse => await TableParse(tableParse, metadata),
            _ => throw new NotSupportedException(
                $"No in-process handler for {outputPayload.GetType().Name} (ServiceType '{outputPayload.ServiceType}')")
        };
    }

    private static RoutingResponse XmlProcessor(XmlProcessorPayload payload, Metadata metadata)
    {
        var output = XmlProcessorEmulator.Process(payload, metadata.GetValue("x-dj-ucpp-source-encoding"));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, output);

        var response = new RoutingResponse(new(), new(), 0);
        response.Payload.Add(ResponseKey, Convert.ToBase64String(stream.ToArray()));
        return response;
    }

    private async Task<RoutingResponse> TableParse(TableParsePayload payload, Metadata metadata)
    {
        var response = new RoutingResponse(new(), new(), 0);
        var nativeCallsBefore = _parserTableParse.Calls.Count;
        try
        {
            // Same call ucpp-tableparse GRPC/UcppService.PerformProcessing makes.
            var result = await _htmlParser.TableParseUsed(_tableParseLogging, metadata, payload, new TableParseMetrics());
            response.Payload.Add(result);
            TableParseCalls.Add(new(payload, result, _parserTableParse.Calls.Skip(nativeCallsBefore).ToList()));
        }
        catch (Exception ex)
        {
            // BaseUcppService turns the exception into an Error on the reply.
            response.Errors.Add(new Error { Code = TableParseErrorCode, Message = ex.ToString() });
            TableParseCalls.Add(new(payload, new() { ["error"] = ex.ToString() }, _parserTableParse.Calls.Skip(nativeCallsBefore).ToList()));
        }
        return response;
    }
}
