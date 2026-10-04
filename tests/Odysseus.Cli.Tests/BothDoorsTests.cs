namespace Odysseus.Cli.Tests;

using System.Text.Json;

using Odysseus.Application;
using Odysseus.Broker;
using Odysseus.Domain;

/// <summary>
/// The rules that hold on both ways in.
/// </summary>
/// <remarks>
/// The command line and the MCP server drive the same services over the same projects root, so a rule
/// enforced on one of them and not on the other is not enforced: whoever it was meant to stop uses the
/// other door. Three of them are worth stating here because all three are about somebody's money - the
/// ceiling on a project's research, the account a connector may reach, and the one measurement on the
/// closed history that cannot be taken twice.
/// </remarks>
// Standard output is one thing for the whole process, so these cannot run beside anything that writes.
[DoNotParallelize]
[TestClass]
public class BothDoorsTests : CliTestBase
{
	/// <summary>
	/// A project started at a terminal is granted what a project started by the server is granted.
	/// </summary>
	/// <remarks>
	/// The budget belongs to the project rather than to whichever door opened it. Were the two to differ,
	/// the smaller one would be a formality: start the project through the other door, and go on working
	/// on it through this one.
	/// </remarks>
	[TestMethod]
	public async Task AProjectStartedHereIsGrantedWhatTheServerGrants()
	{
		AreEqual(0, (await RunAsync("new", "a", "study")).Code);

		var projects = await Workspace.Projects.ListProjectsAsync(CancellationToken);

		AreEqual(1, projects.Count, "the project was not created.");
		AreEqual(ResearchBudget.Default, projects[0].Budget,
			"a project started from the command line was granted a different ceiling from one started by the server.");
	}

	/// <summary>Nothing this program can be told puts it on a real account.</summary>
	[TestMethod]
	public void NothingHereCanReachARealAccount()
	{
		var factory = (StockSharpConnectorFactory)Workspace.Broker.Connectors;

		AreEqual(TradingModes.Paper, factory.Mandate.Mode,
			"the command line assembled a connector factory that is not held to a paper account.");
		IsFalse(factory.Mandate.IsLive, "the command line can reach real money.");
	}

	/// <summary>There is no command here that starts anything trading.</summary>
	[TestMethod]
	public async Task NoCommandStartsARunner()
	{
		foreach (var attempt in new[]
		{
			new[] { "deploy" },
			new[] { "live" },
			new[] { "runner", "start", "dep_one" },
			new[] { "runner", "launch", "dep_one" },
		})
		{
			var answer = await RunAsync(attempt);

			AreEqual(1, answer.Code, $"'{string.Join(' ', attempt)}' was not refused.");
			IsTrue(answer.Says("There is no command"), $"'{string.Join(' ', attempt)}' reached something: {answer.Everything}");
		}
	}

	/// <summary>
	/// The one slice any command names is the one it is allowed to name.
	/// </summary>
	/// <remarks>
	/// Read off the source rather than inferred from a run, because what is being asserted is that there
	/// is no way to ask for the other slices at all. A backtest here is always the development slice, and
	/// the held-out and closed ones are reached only through the services that count how often they have
	/// been looked at - which is the whole mechanism by which a measurement stays honest.
	/// </remarks>
	[TestMethod]
	public void TheOnlySliceAnyCommandNamesIsTheDevelopmentOne()
		=> NamesOnly("DataSlices", "Development");

	/// <summary>And the one mandate any of them names is the paper one.</summary>
	[TestMethod]
	public void TheOnlyMandateAnyCommandNamesIsPaper()
		=> NamesOnly("TradingMandate", "Paper");

	/// <summary>
	/// Nothing here launches a runner, which is what live trading would have to travel through.
	/// </summary>
	/// <remarks>
	/// Live is a property a process has from birth, decided by a file named to the process that trades.
	/// The command line never starts such a process: it stops one, and it ends one, and it prints an
	/// example of the file for a person to write themselves.
	/// </remarks>
	[TestMethod]
	public void NothingHereLaunchesARunner()
	{
		foreach (var (file, text) in Sources())
		{
			// Whole words: the workspace holds a RunnerLauncher, which is how a deployment somebody else
			// started is found and stopped. What it must never do is build one of these and start one.
			IsFalse(Regex.IsMatch(text, @"\bRunnerLaunch\b"),
				$"{Path.GetFileName(file)} builds a launch, so this program can start something that trades.");
			IsFalse(Regex.IsMatch(text, @"\bLaunchAsync\b"),
				$"{Path.GetFileName(file)} launches a runner.");
		}
	}

	/// <summary>The example mandate goes to standard output and what it is for goes beside it.</summary>
	[TestMethod]
	public async Task TheMandateTemplateIsPrintedAndTheAdviceIsPrintedElsewhere()
	{
		var answer = await RunAsync("mandate", "template");

		AreEqual(0, answer.Code, answer.Error);

		IsTrue(answer.Out.Contains("\"phrase\"", StringComparison.Ordinal),
			$"the example did not go to standard output: {answer.Out}");
		IsTrue(answer.Error.Contains(LiveMandateFile.PathVariable, StringComparison.Ordinal),
			$"what to do with the file was not said on standard error: {answer.Error}");
		IsFalse(answer.Out.Contains("Save this where only you can write it", StringComparison.Ordinal),
			"the advice went to standard output, where redirecting it into a file would ruin the file.");
	}

	/// <summary>
	/// What is printed is a mandate that parses, which is the whole point of printing it there.
	/// </summary>
	/// <remarks>
	/// The moment is fixed rather than taken from the clock. The example carries an expiry, and a test
	/// that read the current time would start failing on a date nobody chose, for a reason that has
	/// nothing to do with whether the file parses.
	/// </remarks>
	[TestMethod]
	public async Task WhatIsPrintedIsAMandateThatParses()
	{
		var answer = await RunAsync("mandate", "template");
		var saved = Path.Combine(Root, "mandate.json");

		await File.WriteAllTextAsync(saved, answer.Out, CancellationToken);

		var mandate = LiveMandateFile.Read(saved, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

		IsTrue(mandate.IsLive, "the example reads as a paper mandate, which permits nothing and shows nothing.");
		IsTrue(mandate.Phrase.Length > 0, "the example carries no phrase, so nobody could be asked for one.");
		AreEqual("U1234567", mandate.Account);
	}

	/// <summary>Printing an example writes no file, which is why holding one means something.</summary>
	[TestMethod]
	public async Task PrintingAnExampleWritesNoMandate()
	{
		var before = Files();

		AreEqual(0, (await RunAsync("mandate", "template")).Code);

		AreEqual(
			string.Join('\n', before),
			string.Join('\n', Files()),
			"printing an example mandate left a file behind, and a file this program can write is a file it can be talked into writing.");
	}

	/// <summary>Anything else asked of the mandate command is refused.</summary>
	[TestMethod]
	public async Task TheMandateCommandTakesNothingElse()
	{
		foreach (var args in new[] { new[] { "mandate" }, new[] { "mandate", "write" }, new[] { "mandate", "sign" } })
		{
			var answer = await RunAsync(args);

			AreEqual(1, answer.Code, $"'{string.Join(' ', args)}' was not refused.");
			IsTrue(answer.Says("odysseus mandate template"), $"the refusal does not show the one form: {answer.Error}");
		}
	}

	/// <summary>The mandate the example describes is the shape this build reads.</summary>
	[TestMethod]
	public async Task TheExampleCarriesTheSchemaThisBuildReads()
	{
		var answer = await RunAsync("mandate", "template");

		using var document = JsonDocument.Parse(answer.Out);

		AreEqual(LiveMandateFile.Schema, document.RootElement.GetProperty("schema").GetInt32(),
			"the example names a schema this build does not read, so saving it produces a file that is refused.");
	}

	/// <summary>
	/// Asserts that a type is only ever named through one of its members.
	/// </summary>
	/// <param name="type">The type as it is written in the source.</param>
	/// <param name="member">The only member of it this program may name.</param>
	private static void NamesOnly(string type, string member)
	{
		foreach (var (file, text) in Sources())
		{
			foreach (var named in Regex.Matches(text, Regex.Escape(type) + @"\.(\w+)").Cast<Match>())
			{
				AreEqual(member, named.Groups[1].Value,
					$"{Path.GetFileName(file)} names {type}.{named.Groups[1].Value}, and this program may only name {type}.{member}.");
			}
		}
	}

	/// <summary>
	/// The source of the command line as it is on disk.
	/// </summary>
	/// <returns>Each file and what is in it.</returns>
	private static IEnumerable<(string File, string Text)> Sources()
	{
		var root = Path.Combine(RepositoryRoot, "src", "Odysseus.Cli");
		var built = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
		var intermediate = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";

		var files = Directory
			.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains(built, StringComparison.OrdinalIgnoreCase))
			.Where(f => !f.Contains(intermediate, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		IsTrue(files.Length > 0, "no source was found, so this test is asserting nothing.");

		return [.. files.Select(f => (f, File.ReadAllText(f)))];
	}

	/// <summary>
	/// Everything under the projects root, so a command can be shown to have written nothing.
	/// </summary>
	/// <returns>The paths, in a stable order.</returns>
	private IReadOnlyList<string> Files()
		=> [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)];
}
