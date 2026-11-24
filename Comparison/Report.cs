using TableParseFeedTest.Pipeline;

namespace TableParseFeedTest.Comparison;

public static class Report
{
    private const int MaxDiffLinesOnConsole = 40;

    /// <returns>Process exit code: 0 when every component matches.</returns>
    public static int Print(List<ComponentResult> components, List<TableParseCall> failedTableParse, string outputDirectory)
    {
        using var file = new StreamWriter(Path.Combine(outputDirectory, "comparison.txt"));

        Console.WriteLine();
        Console.WriteLine("Comparison with the regression fixture (ucpp-files-test-automation ComponentsFilter + CommonComparer):");
        foreach (var component in components)
        {
            var status = component.Diff.IsDifferent ? "DIFF" : "OK  ";
            Console.WriteLine($"  {status} {component.Component}");
            file.WriteLine($"### {component.Component}: {(component.Diff.IsDifferent ? "DIFFERENT" : "equal")}");

            if (!component.Diff.IsDifferent)
            {
                continue;
            }

            var lines = component.Diff.Diffs.OrderBy(d => d.Key).SelectMany(d => d.Value).ToList();
            foreach (var line in lines)
            {
                file.WriteLine(line);
            }
            foreach (var line in lines.Take(MaxDiffLinesOnConsole))
            {
                Console.WriteLine($"         {line}");
            }
            if (lines.Count > MaxDiffLinesOnConsole)
            {
                Console.WriteLine($"         ... {lines.Count - MaxDiffLinesOnConsole} more lines in comparison.txt");
            }
        }

        foreach (var call in failedTableParse)
        {
            Console.WriteLine($"  tableparse call failed: {call.Response["error"].Split('\n')[0]}");
        }

        var passed = components.All(c => !c.Diff.IsDifferent) && failedTableParse.Count == 0;
        Console.WriteLine();
        Console.WriteLine(passed ? "PASS: produced story matches the expected output." : "FAIL: produced story differs from the expected output.");
        Console.WriteLine($"artifacts  {outputDirectory}");
        return passed ? 0 : 1;
    }
}
