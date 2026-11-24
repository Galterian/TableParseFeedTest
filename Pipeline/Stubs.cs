using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Com.Dowjones.Ucpp.JsonLogic.Net;
using Com.Dowjones.Ucpp.JsonLogic.Net.Services;
using Newtonsoft.Json.Linq;

namespace TableParseFeedTest.Pipeline;

/// <summary>
/// Implements any of the UCPP logging interfaces (ILogging&lt;T&gt;, IScopedLogging&lt;T&gt;) by forwarding
/// the message argument to a log file, and to the console for warnings and errors.
/// </summary>
public class LogProxy : DispatchProxy
{
    public static TextWriter? LogFile { get; set; }
    public static bool Verbose { get; set; }

    public static T Create<T>() where T : class => Create<T, LogProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null || !targetMethod.Name.StartsWith("Log", StringComparison.Ordinal))
        {
            return null;
        }

        var level = targetMethod.Name[3..];
        var message = args?.FirstOrDefault() as string ?? string.Empty;
        LogFile?.WriteLine($"[{level}] {message}");
        if (Verbose || level is "Error" or "Fatal")
        {
            var oneLine = message.Length > 400 ? message[..400] + "..." : message;
            Console.WriteLine($"  [{level}] {oneLine}");
        }
        return null;
    }
}

public class Sha256HashService : IHashService
{
    public string Create(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty))).ToLowerInvariant();

    public string GetEllipsis(string hash) => hash.Length > 8 ? hash[..8] + "..." : hash;
}

/// <summary>
/// Offline replacements for the enricher operators that need Redis/DynamoDB. They only feed codelines
/// and RTNS components, never the body that tableparse produces.
/// </summary>
public static class OfflineOperators
{
    public static void Register(EvaluateOperators operators, IReadOnlyDictionary<string, JObject> lookupTables, List<string> notes)
    {
        Replace(operators, "lookup", async (p, args, data, headers) =>
        {
            var (value, table, column) = await LookupArguments(p, args, data, headers);
            var found = TryLookup(lookupTables, table, value, column, out var result);
            if (!found)
            {
                notes.Add($"lookup '{value}' in {table}.{column}: not in the offline lookup file, returned \"\"");
            }
            return result;
        });

        Replace(operators, "lookup-exists", async (p, args, data, headers) =>
        {
            var (value, table, column) = await LookupArguments(p, args, data, headers);
            return TryLookup(lookupTables, table, value, column, out _);
        });

        // EnricherOperationManager's version allocates from a Redis INCRBY counter. The E/ codes it
        // produces are dropped by the regression comparison (RemoveDynamicCodes).
        Replace(operators, "chaining-code", async (p, args, data, headers) =>
        {
            var prefix = (await p.ApplyAsync(args[0], data, headers))?.ToString() ?? string.Empty;
            return $"{prefix}00000";
        });
    }

    private static void Replace(EvaluateOperators operators, string name,
        Func<IProcessJsonLogic, JToken[], object, object, Task<object>> operation)
    {
        operators.DeleteOperator(name);
        operators.AddOperator(name, operation);
    }

    private static async Task<(string Value, string Table, string Column)> LookupArguments(
        IProcessJsonLogic p, JToken[] args, object data, object headers)
    {
        var value = (await p.ApplyAsync(args[0], data, headers))?.ToString() ?? string.Empty;
        var table = args.Length > 1 ? (await p.ApplyAsync(args[1], data, headers))?.ToString() ?? string.Empty : string.Empty;
        var column = args.Length > 2 ? (await p.ApplyAsync(args[2], data, headers))?.ToString() ?? string.Empty : string.Empty;
        return (value, table, column);
    }

    private static bool TryLookup(IReadOnlyDictionary<string, JObject> tables, string table, string key, string column, out string result)
    {
        result = string.Empty;
        if (!tables.TryGetValue(table, out var rows) || rows[key] is not JObject row)
        {
            return false;
        }
        result = row[column]?.ToString() ?? string.Empty;
        return true;
    }
}
