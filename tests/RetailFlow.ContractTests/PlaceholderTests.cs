namespace RetailFlow.ContractTests;

/// <summary>
/// No inter-service HTTP client exists yet to pin a Pact contract to - this project
/// is wired (PactNet referenced, builds, runs in CI) so the FIRST real contract test
/// only has to add a test, not stand up the project. See docs/TESTING.md.
/// </summary>
public class PlaceholderTests
{
    [Fact(Skip = "No consumer/provider pair exists yet - see class remarks.")]
    public void FirstContractTestGoesHere()
    {
    }
}
