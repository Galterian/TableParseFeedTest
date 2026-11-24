using Newtonsoft.Json;
using TableParseFeedTest.Pipeline;

namespace TableParseFeedTest.Comparison;

/// <summary>Intermediate results of each hop, for diffing a failure down to the tableparse call that caused it.</summary>
public static class Artifacts
{
    public static void Write(string outputDirectory, PipelineResult result)
    {
        File.WriteAllText(Path.Combine(outputDirectory, "normalized-data.json"),
            JsonConvert.SerializeObject(result.NormalizedData, Formatting.Indented));
        File.WriteAllText(Path.Combine(outputDirectory, "enriched-data.json"),
            JsonConvert.SerializeObject(result.EnrichedData, Formatting.Indented));

        var tableParseDirectory = Path.Combine(outputDirectory, "tableparse");
        if (Directory.Exists(tableParseDirectory))
        {
            Directory.Delete(tableParseDirectory, recursive: true);
        }
        Directory.CreateDirectory(tableParseDirectory);

        for (var i = 0; i < result.TableParseCalls.Count; i++)
        {
            var call = result.TableParseCalls[i];
            var prefix = Path.Combine(tableParseDirectory, $"{i + 1:D2}");
            File.WriteAllText($"{prefix}.input.html", call.Payload.Contents);
            File.WriteAllLines($"{prefix}.request.txt", new[]
                {
                    $"ContentType: {call.Payload.ContentType}",
                    $"IsBody: {call.Payload.IsBody}",
                    $"IgnoreNonAsciiEncoding: {call.Payload.IgnoreNonAsciiEncoding}",
                    $"QuerySet: {call.Payload.QuerySet}",
                    $"ResponseTypes: {string.Join(";", call.Payload.ResponseTypes ?? new())}",
                    "Arguments:",
                }
                .Concat((call.Payload.TableParseCommandArguments ?? new()).Select(a => $"  {a.Key}={a.Value}"))
                .Append("Native calls:")
                .Concat(call.NativeCalls.Select(c => $"  {c}")));
            foreach (var (key, value) in call.Response)
            {
                File.WriteAllText($"{prefix}.{key}.txt", value);
            }
        }
    }
}
