using Com.Dowjones.Ucpp.Mesh.TableParser.Managed;
using Com.Dowjones.Ucpp.Mesh.TableParserWrapper;
using Com.Dowjones.Ucpp.Mesh.TableParserWrapper.Models;
using TableParseFeedTest.Native;

namespace TableParseFeedTest.Pipeline;

/// <summary>
/// One wrapper call exactly as ucpp-tableparse made it, for replay by
/// ucpp-parser-table-parse's TableParser.Compare (<c>NN.calls.json</c>). <see cref="Result"/> is the
/// wrapper's raw result (base64 for the Get*Output calls).
/// </summary>
public record CapturedStep(string Call, string[] Args, bool? Success = null, string Result = null);

/// <summary>
/// The real <see cref="ParserTableParse"/> with the two production file-system assumptions mapped onto
/// this machine: the query-set directory baked into ucpp-tableparse's HtmlParser, and the reload of
/// <see cref="TableParserLibrary.WrapperLibraryPath"/> inside ResetTableParse. With <c>managed</c> the calls go to
/// ucpp-parser-table-parse's C# port (<see cref="ManagedParserTableParse"/>) instead.
/// </summary>
public class LocalParserTableParse : IParserTableParse
{
    // ucpp-tableparse Html/HtmlParser.cs DEFAULT_QUERY_SET_PATH
    public const string ProductionDatabasePath = "/nss/rtns/v_running/database/";

    private readonly IParserTableParse _inner;
    private readonly bool _managed;
    private readonly string _databasePath;
    private bool _reportedResetFallback;

    public LocalParserTableParse(string databasePath, bool managed = false)
    {
        _databasePath = databasePath.TrimEnd('/') + "/";
        _managed = managed;
        _inner = managed ? new ManagedParserTableParse() : new ParserTableParse();
    }

    public List<string> Calls { get; } = new();

    public List<CapturedStep> Steps { get; } = new();

    public TableParseResponse ResetTableParse()
    {
        Steps.Add(new("ResetTableParse", Array.Empty<string>()));
        var response = _inner.ResetTableParse();
        if (response.Success || _managed)
        {
            Calls.Add("ResetTableParse");
            return response;
        }

        // Nothing installed at /usr/local/lib, so the wrapper bailed out before the native Reset().
        TableParserLibrary.Reset();
        if (!_reportedResetFallback)
        {
            Console.WriteLine($"  note: {response.ErrorMessage}");
            Console.WriteLine("        native Reset() called directly on the library under test instead. To run the wrapper's");
            Console.WriteLine($"        own reload: sudo ln -sfn {Path.GetFullPath(TableParserLibrary.LoadedPath)} {TableParserLibrary.WrapperLibraryPath}");
            _reportedResetFallback = true;
        }
        Calls.Add("ResetTableParse (native Reset fallback)");
        return new TableParseResponse { Success = true, ErrorMessage = string.Empty };
    }

    public TableParseResponse SetTableParseOptions(string option, string optionValue)
    {
        Calls.Add($"SetTableParseOptions {option}={optionValue}");
        Steps.Add(new("SetTableParseOptions", new[] { option, optionValue }));
        return _inner.SetTableParseOptions(option, optionValue);
    }

    public TableParseResponse SetQuerySetFile(string path)
    {
        var mapped = MapQuerySet(path);
        Steps.Add(new("SetQuerySetFile", new[] { mapped }));
        return _inner.SetQuerySetFile(mapped);
    }

    public TableParseResponse SetQuerySetISMFile(string path)
    {
        var mapped = MapQuerySet(path);
        Steps.Add(new("SetQuerySetISMFile", new[] { mapped }));
        return _inner.SetQuerySetISMFile(mapped);
    }

    public TableParseResults ParseTableData(string base64XHtmlContents, bool isBody)
    {
        Calls.Add($"ParseTableData isBody={isBody}");
        return Record("ParseTableData", new[] { base64XHtmlContents, isBody.ToString() },
            _inner.ParseTableData(base64XHtmlContents, isBody));
    }

    public TableParseResults ParseTableADAData(string base64XHtmlContents, bool isBody)
    {
        Calls.Add($"ParseTableADAData isBody={isBody}");
        return Record("ParseTableADAData", new[] { base64XHtmlContents, isBody.ToString() },
            _inner.ParseTableADAData(base64XHtmlContents, isBody));
    }

    public TableParseResults GetPlainTextOutput() => Record("GetPlainTextOutput", Array.Empty<string>(), _inner.GetPlainTextOutput());

    public TableParseResults GetADATextOutput() => Record("GetADATextOutput", Array.Empty<string>(), _inner.GetADATextOutput());

    public TableParseResults GetX2EOutput() => Record("GetX2EOutput", Array.Empty<string>(), _inner.GetX2EOutput());

    public void Dispose() => _inner.Dispose();

    private TableParseResults Record(string call, string[] args, TableParseResults results)
    {
        Steps.Add(new(call, args, results.Success, results.Result));
        return results;
    }

    private string MapQuerySet(string path)
    {
        var mapped = path.StartsWith(ProductionDatabasePath, StringComparison.Ordinal)
            ? _databasePath + path[ProductionDatabasePath.Length..]
            : path;

        // The wrapper discards LoadQuerySetFile's result, so a missing file would only show up as
        // wrong output. Fail loudly instead.
        if (!File.Exists(mapped))
        {
            throw new FileNotFoundException($"Query set not found: {mapped} (requested {path})", mapped);
        }

        Calls.Add($"SetQuerySetFile {mapped}");
        return mapped;
    }
}
