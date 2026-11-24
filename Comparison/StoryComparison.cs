using FilesAutomationTest.Extensions.StoriesComparison;
using FilesAutomationTest.Models;
using FilesAutomationTest.Models.StoriesTests;
using FilesAutomationTest.Services.Comparers;

namespace TableParseFeedTest.Comparison;

public record ComponentResult(string Component, string Expected, string Actual, DiffCheckerResult Diff);

/// <summary>
/// The NDF branch of ucpp-files-test-automation's RegressionTestsComparer.CompareStories: both stories
/// are reduced with ComponentsFilter (pbody) and compared component by component with CommonComparer.
/// </summary>
public static class StoryComparison
{
    public static List<ComponentResult> Compare(string actualStory, string expectedStory, Provider provider)
    {
        var filter = new ComponentsFilter();
        var comparer = new CommonComparer();

        if (!filter.VerifyXml(expectedStory))
        {
            throw new InvalidOperationException("Expected story is not a valid NDF XML story");
        }
        if (!filter.VerifyXml(actualStory))
        {
            throw new InvalidOperationException("Produced story is not a valid NDF XML story");
        }

        var expected = filter.PopulateUcppStoryComponents(expectedStory, "pbody", provider);
        var actual = filter.PopulateUcppStoryComponents(actualStory, "pbody", provider);

        return new List<ComponentResult>
        {
            Component("routing", expected.Routing.Trim(), actual.Routing.Trim()),
            Component("Xml Version", expected.XmlVersion, actual.XmlVersion),
            Component("Ndf Version", expected.NdfVersion, actual.NdfVersion),
            Component("headline", expected.Headline, actual.Headline),
            Component("codes", expected.Codes, actual.Codes),
            Component("body", expected.Body, actual.Body),
        };

        ComponentResult Component(string name, string expectedValue, string actualValue)
            => new(name, expectedValue, actualValue, comparer.CompareComponentsValues(name, actualValue, expectedValue));
    }

    public static StoryComponents Components(string story, Provider provider)
        => new ComponentsFilter().PopulateUcppStoryComponents(story, "pbody", provider);
}
