using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using Com.Dowjones.Ucpp.Payloads.Payloads.BasicPayloads.XmlProcessor;

namespace TableParseFeedTest.Pipeline;

/// <summary>
/// Answers the normalizer's <c>normalize-xml</c> call-out in-process.
///
/// ucpp-xmlprocessor is not part of the chain under test (and not in this workspace), so this is a
/// port of the code path the BWF manifest exercises: <c>XmlV2/XmlProcessorV2.cs</c> with the default
/// <c>Auto</c> strategy, unencoded XPath queries against an <c>XmlDocument</c>, plus the
/// <c>Encoding</c> and <c>EntireXml</c> query kinds. Encoded (HtmlAgilityPack) queries are rejected
/// rather than approximated.
/// </summary>
public static class XmlProcessorEmulator
{
    private const string DefaultEncoding = "UTF-8";
    private const int EncodingScanLength = 500;
    private const string EncodingAttribute = "encoding=\"";

    // ProcessorsExtensionsV2.NamespaceStripRegex
    private static readonly Regex NamespaceStripRegex =
        new(@"\s+xmlns(?!:)\s*=\s*""[^""]*""" +
            @"|(?<t1></?)[\w.-]+:(?<t2>[\w.-]+)" +
            @"|(?<a1>\s)[\w.-]+:(?<a2>[\w.-]+\s*=)",
            RegexOptions.Compiled);

    // XmlProcessorV2.XPathPrefixRegex
    private static readonly Regex XPathPrefixRegex =
        new(@"(?<![:\w])([A-Za-z_][\w.-]*):(?![:])", RegexOptions.Compiled);

    public static XmlProcessorOutput Process(XmlProcessorPayload payload, string? sourceEncoding)
    {
        if (payload.XmlQueries == null || !payload.XmlQueries.Any())
        {
            throw new NotSupportedException("Only XmlQueries are emulated; XSL templates are not.");
        }
        if (payload.UseHtmlParser)
        {
            throw new NotSupportedException("The legacy HtmlProcessor path (UseHtmlParser) is not emulated.");
        }

        var queries = payload.XmlQueries.ToList();
        var contents = payload.Contents;

        var encodingName = !string.IsNullOrEmpty(sourceEncoding) ? sourceEncoding : GetEncodingFromContent(contents);
        var text = Encoding.GetEncoding(encodingName).GetString(contents, GetBomLength(contents), contents.Length - GetBomLength(contents));
        var xmlText = RemoveAllXmlNamespace(text);

        XmlDocument? xmlDocument = null;
        if (queries.Any(q => q.Method == XmlQueryType.XPath && q.Encoded is not true))
        {
            xmlDocument = new XmlDocument { PreserveWhitespace = true };
            xmlDocument.LoadXml(xmlText);
        }

        var results = new Dictionary<string, List<string>>(queries.Count);
        foreach (var query in queries)
        {
            var matches = query.Method switch
            {
                XmlQueryType.Encoding => new List<string> { ExtractEncoding(text) },
                XmlQueryType.EntireXml => new List<string> { EncodeNonIso(text, query.NonIsoSymbolHandling) },
                XmlQueryType.XPath when query.Encoded is true =>
                    throw new NotSupportedException($"Encoded XPath query '{query.Name}' (HtmlAgilityPack path) is not emulated."),
                XmlQueryType.XPath => EvaluateViaXml(query, xmlDocument!),
                _ => new List<string>()
            };

            if (!query.ExpectMultipleValues && matches.Count > 1)
            {
                matches = new List<string> { matches[0] };
            }

            if (query.StripCData && matches.Count > 0)
            {
                for (var i = 0; i < matches.Count; i++)
                {
                    matches[i] = matches[i]
                        .Replace("<![CDATA[", string.Empty)
                        .Replace("&lt;![CDATA[", string.Empty)
                        .Replace("]]>", string.Empty)
                        .Replace("]]&gt;", string.Empty)
                        .Trim();
                }
            }

            results.Add(query.Name, matches);
        }

        return new XmlProcessorOutput(results);
    }

    private static List<string> EvaluateViaXml(XmlQuery query, XmlDocument xmlDocument)
    {
        var results = new List<string>();
        var nodes = xmlDocument.SelectNodes(StripXPathPrefixes(query.Value));
        if (nodes == null || nodes.Count == 0)
        {
            results.Add(string.Empty);
            return results;
        }

        foreach (XmlNode? node in nodes)
        {
            var raw = node is null ? string.Empty : ExtractXmlValue(node, query);
            results.Add(EncodeNonIso(HtmlDecode(raw), query.NonIsoSymbolHandling));
        }

        return results;
    }

    private static string ExtractXmlValue(XmlNode node, XmlQuery query)
    {
        if (query.ValueSelector is AttributeXmlNodeValueSelector attr)
        {
            var colon = attr.Name.IndexOf(':');
            var localName = colon >= 0 ? attr.Name[(colon + 1)..] : attr.Name;
            return node.Attributes?[localName]?.Value ?? string.Empty;
        }

        return query.ValueSelector?.GetValue(node) ?? node.InnerXml ?? string.Empty;
    }

    private static string RemoveAllXmlNamespace(string xmlData)
    {
        if (string.IsNullOrEmpty(xmlData) ||
            (xmlData.IndexOf(':') < 0 && xmlData.IndexOf("xmlns", StringComparison.Ordinal) < 0))
        {
            return xmlData;
        }

        return NamespaceStripRegex.Replace(xmlData, "${t1}${t2}${a1}${a2}");
    }

    private static string StripXPathPrefixes(string xpath)
    {
        if (string.IsNullOrEmpty(xpath) || xpath.IndexOf(':') < 0)
        {
            return xpath;
        }

        var sb = new StringBuilder(xpath.Length);
        var i = 0;
        while (i < xpath.Length)
        {
            var c = xpath[i];
            if (c == '\'' || c == '"')
            {
                var start = i++;
                while (i < xpath.Length && xpath[i] != c)
                {
                    i++;
                }
                if (i < xpath.Length)
                {
                    i++;
                }
                sb.Append(xpath, start, i - start);
                continue;
            }

            var runStart = i;
            while (i < xpath.Length && xpath[i] != '\'' && xpath[i] != '"')
            {
                i++;
            }
            sb.Append(XPathPrefixRegex.Replace(xpath.Substring(runStart, i - runStart), string.Empty));
        }

        return sb.ToString();
    }

    // ProcessorsExtensionsV2.HtmlDecodeV2: decode until stable.
    private static string HtmlDecode(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('&') < 0)
        {
            return text;
        }

        var decoded = text;
        string previous;
        do
        {
            previous = decoded;
            decoded = HttpUtility.HtmlDecode(decoded);
        } while (decoded != previous);

        return decoded;
    }

    // Xml/SymbolService.EncodeNonIso
    private static string EncodeNonIso(string text, NonIsoSymbolHandling type)
    {
        if (type == NonIsoSymbolHandling.Preserve || (int)type == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (i + 1 < text.Length && char.IsHighSurrogate(ch) && char.IsLowSurrogate(text[i + 1]))
            {
                var utf32 = char.ConvertToUtf32(ch, text[i + 1]);
                sb.Append(type == NonIsoSymbolHandling.EncodeHex ? $"&#x{utf32:X};" : $"&#{utf32};");
                i++;
            }
            else if (ch < 0x20 || ch > 0x7f)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append(type == NonIsoSymbolHandling.EncodeHex ? $"&#x{(int)ch:X};" : $"&#{(int)ch};");
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string ExtractEncoding(string text)
    {
        var declEnd = text.IndexOf("?>", StringComparison.Ordinal);
        if (declEnd == -1) return string.Empty;

        var limit = Math.Min(declEnd + 2, text.Length);
        var start = text.IndexOf(EncodingAttribute, 0, limit, StringComparison.OrdinalIgnoreCase);
        if (start == -1) return string.Empty;

        start += EncodingAttribute.Length;
        var end = text.IndexOf('"', start);
        return end == -1 ? string.Empty : text.Substring(start, end - start);
    }

    // ProcessorsExtensionsV2.GetEncodingFromContentV2
    private static string GetEncodingFromContent(byte[] contents)
    {
        if (contents == null || contents.Length < 4)
        {
            return DefaultEncoding;
        }

        var bomEncoding = DetectEncodingFromBom(contents);
        var scanLength = Math.Min(contents.Length, EncodingScanLength);
        var candidates = bomEncoding != null
            ? new[] { bomEncoding }
            : new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 };

        foreach (var candidate in candidates)
        {
            try
            {
                var header = candidate.GetString(contents, 0, scanLength);
                var start = header.IndexOf(EncodingAttribute, StringComparison.OrdinalIgnoreCase);
                if (start == -1) continue;

                start += EncodingAttribute.Length;
                var end = header.IndexOf('"', start);
                if (end == -1 || end <= start) continue;

                var encodingName = header.Substring(start, end - start).Trim();
                var declared = Encoding.GetEncoding(encodingName);
                if (bomEncoding == null && declared is UnicodeEncoding or UTF32Encoding)
                {
                    return candidate.WebName;
                }

                return encodingName;
            }
            catch
            {
                // try the next candidate
            }
        }

        return bomEncoding?.WebName ?? DefaultEncoding;
    }

    private static Encoding? DetectEncodingFromBom(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) return new UTF32Encoding(true, true);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00) return Encoding.UTF32;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        return null;
    }

    private static int GetBomLength(byte[] b)
    {
        if (b.Length >= 4 && ((b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) ||
                              (b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00))) return 4;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return 3;
        if (b.Length >= 2 && ((b[0] == 0xFE && b[1] == 0xFF) || (b[0] == 0xFF && b[1] == 0xFE))) return 2;
        return 0;
    }
}
