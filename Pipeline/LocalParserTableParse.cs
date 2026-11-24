using Com.Dowjones.Ucpp.Mesh.TableParserWrapper;
using Com.Dowjones.Ucpp.Mesh.TableParserWrapper.Models;
using TableParseFeedTest.Native;

namespace TableParseFeedTest.Pipeline;

/// <summary>
/// The real <see cref="ParserTableParse"/> with the two production file-system assumptions mapped onto
/// this machine: the query-set directory baked into ucpp-tableparse's HtmlParser, and the reload of
/// <see cref="TableParserLibrary.WrapperLibraryPath"/> inside ResetTableParse.
/// </summary>
public class LocalParserTableParse : IParserTableParse
{
    // ucpp-tableparse Html/HtmlParser.cs DEFAULT_QUERY_SET_PATH
    public const string ProductionDatabasePath = "/nss/rtns/v_running/database/";

    private readonly ParserTableParse _inner = new();
    private readonly string _databasePath;
    private bool _reportedResetFallback;

    public LocalParserTableParse(string databasePath)
    {
        _databasePath = databasePath.TrimEnd('/') + "/";
    }

    public List<string> Calls { get; } = new();

    public TableParseResponse ResetTableParse()
    {
        var response = _inner.ResetTableParse();
        if (response.Success)
        {
            Calls.Add("ResetTableParse");
            return response;
        }

        // Nothing installed at /usr/local/lib, so the wrapper bailed out before the native Reset().
        TableParserLibrary.Reset();
        if (!_reportedResetFallback)
        {
            Console.WriteLine($"  note: {response.ErrorMessage}");
            Console.WriteLine("        native Reset() called directly on the library under test instead.");
            _reportedResetFallback = true;
        }
        Calls.Add("ResetTableParse (native Reset fallback)");
        return new TableParseResponse { Success = true, ErrorMessage = string.Empty };
    }

    public TableParseResponse SetTableParseOptions(string option, string optionValue)
    {
        Calls.Add($"SetTableParseOptions {option}={optionValue}");
        return _inner.SetTableParseOptions(option, optionValue);
    }

    public TableParseResponse SetQuerySetFile(string path) => _inner.SetQuerySetFile(MapQuerySet(path));

    public TableParseResponse SetQuerySetISMFile(string path) => _inner.SetQuerySetISMFile(MapQuerySet(path));

    public TableParseResults ParseTableData(string base64XHtmlContents, bool isBody)
    {
        Calls.Add($"ParseTableData isBody={isBody}");
        return _inner.ParseTableData(base64XHtmlContents, isBody);
    }

    public TableParseResults ParseTableADAData(string base64XHtmlContents, bool isBody)
    {
        Calls.Add($"ParseTableADAData isBody={isBody}");
        return _inner.ParseTableADAData(base64XHtmlContents, isBody);
    }

    public TableParseResults GetPlainTextOutput() => _inner.GetPlainTextOutput();

    public TableParseResults GetADATextOutput() => _inner.GetADATextOutput();

    public TableParseResults GetX2EOutput() => _inner.GetX2EOutput();

    public void Dispose() => _inner.Dispose();

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
