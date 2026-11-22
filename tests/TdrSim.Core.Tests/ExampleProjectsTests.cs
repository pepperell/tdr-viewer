using TdrSim.Core.Engine;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// The Examples menu must never ship a broken document: every example compiles against
/// the built-in catalog with zero issues, simulates, and round-trips the serializer.
/// </summary>
public class ExampleProjectsTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var example in ExampleProjects.All)
            data.Add(example.Name);
        return data;
    }

    private static ExampleProject Get(string name) => ExampleProjects.All.Single(e => e.Name == name);

    [Fact]
    public void ThereIsABunchAndNamesAreUnique()
    {
        Assert.True(ExampleProjects.All.Count >= 12, $"Only {ExampleProjects.All.Count} examples.");
        Assert.Equal(ExampleProjects.All.Count, ExampleProjects.All.Select(e => e.Name).Distinct().Count());
        Assert.All(ExampleProjects.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryExampleCompilesCleanAndSimulates(string name)
    {
        var example = Get(name);
        var compilation = DocumentCompiler.Compile(example.Document, BuiltInCatalog);
        Assert.Empty(compilation.Issues);
        Assert.NotNull(compilation.Model);
        Assert.NotNull(compilation.Instrument);

        // Correctness smoke at a small grid cap - fast, and the Hermitian self-check
        // still guards the physics path end to end.
        var trace = TdrSimulator.Simulate(
            compilation.Model!, compilation.Instrument!, new SimulationSettings { MaxLog2M = 14 });
        AssertHermitian(trace);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryExampleRoundTripsTheSerializer(string name)
    {
        string json = ProjectSerializer.Serialize(Get(name).Document);
        Assert.Equal(json, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(json)));
    }
}
