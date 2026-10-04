namespace Odysseus.Application.Tests;

/// <summary>
/// The scenarios a run may be measured under, held against the place an agent picks one from.
/// </summary>
[TestClass]
public class RunScenarioTests : OdysseusTestBase
{
	/// <summary>
	/// A scenario the code accepts and the tool does not name is reachable only by guessing the name or
	/// by reading the error a wrong one produces. Nothing else documents the tools, so the argument that
	/// takes a scenario has to name every scenario there is, and a scenario added later fails here until
	/// its description catches up.
	/// </summary>
	[TestMethod]
	public void EveryScenarioIsNamedWhereOneIsChosen()
	{
		var source = File.ReadAllText(
			Path.Combine(RepositoryRoot, "src", "Odysseus.Server", "BacktestTools.cs"));

		var published = ArgumentDescription(source, "scenario");

		foreach (var scenario in RunScenario.All)
		{
			IsTrue(published.Contains($"'{scenario.Name}'", StringComparison.Ordinal),
				$"run_backtest accepts the scenario '{scenario.Name}' and does not say so, so it can be " +
				$"reached only by guessing its name. What it publishes instead: {published}");
		}
	}

	private static string ArgumentDescription(string source, string name)
	{
		var declaration = source.IndexOf($"string {name},", StringComparison.Ordinal);

		IsTrue(declaration > 0, $"run_backtest no longer takes an argument called '{name}'.");

		var description = source.LastIndexOf("[Description(", declaration, StringComparison.Ordinal);

		IsTrue(description > 0, $"the argument '{name}' is published without a description.");

		return source[description..declaration];
	}
}
