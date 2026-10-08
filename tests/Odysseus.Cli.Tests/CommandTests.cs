namespace StockSharp.Odysseus.Cli.Tests;

using StockSharp.Odysseus.Spec;

/// <summary>
/// What the commands promise a person at a terminal.
/// </summary>
/// <remarks>
/// The command line is the other door onto the same research, and a person reads two things off it: the
/// text, and the exit code they wrote a shell script against. Both are asserted here, because the second
/// is the half nobody notices is wrong - a command that printed a refusal and exited zero reads as a
/// success to everything downstream of it.
/// </remarks>
// Standard output is one thing for the whole process, so these cannot run beside anything that writes.
[DoNotParallelize]
[TestClass]
public class CommandTests : CliTestBase
{
	/// <summary>Typing the program's name alone says what it can do.</summary>
	[TestMethod]
	public async Task NothingAtAllPrintsWhatTheCommandsAre()
	{
		var answer = await RunAsync();

		AreEqual(0, answer.Code, "asking for nothing is not a failure.");
		IsTrue(answer.Says("research from the command line"), $"the commands were not printed: {answer.Out}");
	}

	/// <summary>Help is asked for in three ways and answers the same in all of them.</summary>
	[TestMethod]
	public async Task EveryUsualWayOfAskingForHelpIsOne()
	{
		foreach (var asked in new[] { "help", "--help", "-h" })
		{
			var answer = await RunAsync(asked);

			AreEqual(0, answer.Code, $"'{asked}' was not understood as a request for help.");
			IsTrue(answer.Says("what it does"), $"'{asked}' printed no table of commands.");
		}
	}

	/// <summary>A command that does not exist is refused, and the ones that do are shown.</summary>
	[TestMethod]
	public async Task AnUnknownCommandIsRefusedAndShownWhatThereIs()
	{
		var answer = await RunAsync("frobnicate");

		AreEqual(1, answer.Code, "an unknown command exited as though it had worked.");
		IsTrue(answer.Error.Contains("There is no command 'frobnicate'", StringComparison.Ordinal),
			$"the refusal did not name what was typed: {answer.Error}");
		IsTrue(answer.Out.Contains("what it does", StringComparison.Ordinal),
			"a person who mistyped a command was not shown the commands.");
	}

	/// <summary>A refusal goes to standard error, so redirecting the output does not swallow it.</summary>
	[TestMethod]
	public async Task ARefusalIsWrittenWhereARedirectedOutputCannotSwallowIt()
	{
		var answer = await RunAsync("runs");

		AreEqual(1, answer.Code);
		IsTrue(answer.Error.Length > 0, "the refusal was not written to standard error.");
		IsFalse(answer.Out.Contains("No project yet", StringComparison.Ordinal),
			"the refusal was written to standard output as well, where a redirect would hide it.");
	}

	/// <summary>Every command that runs is a command the help mentions.</summary>
	[TestMethod]
	public async Task TheHelpNamesEveryCommandThereIs()
	{
		var listed = Listed((await RunAsync("help")).Out);

		foreach (var command in Dispatched())
		{
			IsTrue(listed.Contains(command),
				$"'{command}' is a command and the help does not mention it, so nobody at a terminal can find it.");
		}
	}

	/// <summary>And every command the help mentions is one that runs.</summary>
	[TestMethod]
	public async Task TheHelpNamesNothingThatIsNotACommand()
	{
		var dispatched = Dispatched();

		foreach (var listed in Listed((await RunAsync("help")).Out).Where(l => l != "command"))
		{
			IsTrue(dispatched.Contains(listed),
				$"the help offers '{listed}' and nothing dispatches it, so typing it answers that there is no such command.");
		}
	}

	/// <summary>Starting a project remembers it, so the next command needs no identifier.</summary>
	[TestMethod]
	public async Task StartingAProjectRemembersIt()
	{
		var started = await RunAsync("new", "a", "study");

		AreEqual(0, started.Code, started.Error);

		var project = Workspace.Current();

		IsTrue(project.Value.Length > 0, "nothing was remembered as the current project.");

		var listed = await RunAsync("projects");

		AreEqual(0, listed.Code, listed.Error);
		IsTrue(listed.Says("a study"), $"the project that was just started is not in the listing: {listed.Out}");
	}

	/// <summary>Before anything is started, the answer is how to start something.</summary>
	[TestMethod]
	public async Task WithoutAProjectTheAnswerSaysHowToStartOne()
	{
		var answer = await RunAsync("runs");

		AreEqual(1, answer.Code, "a command with no project to work on reported success.");
		IsTrue(answer.Says("odysseus new"), $"the refusal does not say how to start one: {answer.Error}");
	}

	/// <summary>An empty projects root is not an error, and says what to do about it.</summary>
	[TestMethod]
	public async Task AnEmptyRootSaysHowToStartAProject()
	{
		var answer = await RunAsync("projects");

		AreEqual(0, answer.Code, "an empty listing is not a failure.");
		IsTrue(answer.Says("odysseus new"), $"nothing said how to start a project: {answer.Out}");
	}

	/// <summary>The generated bars are reported as generated, wherever they are printed.</summary>
	[TestMethod]
	public async Task TheDemoDataSaysItWasGenerated()
	{
		AreEqual(0, (await RunAsync("new", "a", "study")).Code);

		var answer = await RunAsync("demo");

		AreEqual(0, answer.Code, answer.Error);
		IsTrue(answer.Says("generated"), $"the demo data was printed without saying it was invented: {answer.Out}");
		IsTrue(answer.Says("says nothing about any market"),
			$"nothing warned what a result measured on these bars is worth: {answer.Out}");
	}

	/// <summary>A hypothesis is recorded against the project and remembered for the next command.</summary>
	[TestMethod]
	public async Task AHypothesisIsRecordedAndRemembered()
	{
		await StartAProjectWithDataAsync();

		var answer = await RunAsync("propose", SampleHypothesis);

		AreEqual(0, answer.Code, answer.Error);
		IsTrue(answer.Says("revision 1"), $"the revision it was recorded as was not printed: {answer.Out}");
		IsTrue(Workspace.CurrentSpec().Value.Length > 0, "the hypothesis was not remembered for the next command.");
	}

	/// <summary>One that opens no position is refused, with the thing that would fix it.</summary>
	[TestMethod]
	public async Task AHypothesisThatOpensNothingIsRefusedWithWhatWouldFixIt()
	{
		await StartAProjectWithDataAsync();

		var spec = SpecJson.Read(await File.ReadAllTextAsync(SampleHypothesis, CancellationToken));
		var broken = Path.Combine(Root, "opens-nothing.json");

		await File.WriteAllTextAsync(broken, SpecJson.Write(spec with { Entries = [] }), CancellationToken);

		var answer = await RunAsync("propose", broken);

		AreEqual(1, answer.Code, "a hypothesis that opens no position was accepted.");
		IsTrue(answer.Says("never opens a position"), $"the refusal does not say what is wrong: {answer.Out}");
		IsTrue(answer.Says("Add at least one entry rule"), $"the refusal does not say what would fix it: {answer.Out}");

		Throws<InvalidOperationException>(() => Workspace.CurrentSpec(),
			"a hypothesis that was refused was remembered as the current one.");
	}

	/// <summary>
	/// What the build compiled can be read, which is what the build says it can.
	/// </summary>
	/// <remarks>
	/// The walkthrough as far as it goes without a worker process: a project, the generated bars, the
	/// published sample hypothesis, and the strategy translated and compiled from it. A generated
	/// strategy is ordinary C# and a person is entitled to read it before believing anything measured on
	/// it, which is why the build ends by saying so - and why the command it names has to exist.
	/// </remarks>
	[TestMethod]
	public async Task WhatWasCompiledCanBeRead()
	{
		await StartAProjectWithDataAsync();

		AreEqual(0, (await RunAsync("propose", SampleHypothesis)).Code, "the sample hypothesis was refused.");

		var built = await RunAsync("build");

		AreEqual(0, built.Code, built.Error);

		var candidate = await Workspace.Candidates.GetAsync(
			Workspace.Current(), Workspace.CurrentCandidate(), CancellationToken);

		IsTrue(built.Says(candidate.ClassName), $"the build did not say what it had compiled: {built.Out}");

		var source = await RunAsync("source");

		AreEqual(0, source.Code, source.Error);
		IsTrue(source.Out.Contains($"class {candidate.ClassName}", StringComparison.Ordinal),
			$"the C# the build compiled was not printed: {source.Out}");
	}

	/// <summary>Proposing nothing names the argument it wanted.</summary>
	[TestMethod]
	public async Task ProposeWithoutAFileNamesTheOneItWants()
	{
		await StartAProjectWithDataAsync();

		var answer = await RunAsync("propose");

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("odysseus propose"), $"the refusal does not show the form: {answer.Error}");
	}

	/// <summary>Proposing a file that is not there says which file.</summary>
	[TestMethod]
	public async Task ProposeOfAFileThatIsNotThereSaysWhichFile()
	{
		await StartAProjectWithDataAsync();

		var missing = Path.Combine(Root, "not-here.json");
		var answer = await RunAsync("propose", missing);

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("not-here.json"), $"the refusal does not name the file that is missing: {answer.Error}");
	}

	/// <summary>Each step of the walkthrough names the step before it when that one has not happened.</summary>
	[TestMethod]
	public async Task EveryStepSaysWhichStepIsMissing()
	{
		await StartAProjectWithDataAsync();

		foreach (var (command, expected) in new[]
		{
			("build", "odysseus propose"),
			("source", "odysseus build"),
			("backtest", "odysseus build"),
			("measure", "odysseus build"),
			("closed", "odysseus build"),
			("explain", "odysseus backtest"),
		})
		{
			var answer = await RunAsync(command);

			AreEqual(1, answer.Code, $"'{command}' reported success with nothing to work on.");
			IsTrue(answer.Says(expected), $"'{command}' does not say to run {expected} first: {answer.Error}");
		}
	}

	/// <summary>Calling a study finished without saying why is refused.</summary>
	[TestMethod]
	public async Task CompletingWithoutSayingWhyIsRefused()
	{
		await StartAProjectWithDataAsync();

		foreach (var args in new[] { new[] { "complete" }, new[] { "complete", "   " } })
		{
			var answer = await RunAsync(args);

			AreEqual(1, answer.Code, "a completion with no reason was accepted.");
			IsTrue(answer.Says("why you consider it finished"), $"the refusal does not say what is wanted: {answer.Error}");
		}
	}

	/// <summary>Nothing finished yet is an answer rather than a failure.</summary>
	[TestMethod]
	public async Task CompletedSaysWhenNothingIsFinished()
	{
		await StartAProjectWithDataAsync();

		var answer = await RunAsync("completed");

		AreEqual(0, answer.Code, answer.Error);
		IsTrue(answer.Says("Nothing finished yet"), $"an empty listing said something else: {answer.Out}");
	}

	/// <summary>Importing without naming a symbol shows the form.</summary>
	[TestMethod]
	public async Task ImportWithoutASymbolNamesTheFormItWants()
	{
		var answer = await RunAsync("import");

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("odysseus import NVDA"), $"the refusal does not show the form: {answer.Error}");
	}

	/// <summary>Importing with no broker says which variables would give it one.</summary>
	[TestMethod]
	public async Task ImportWithoutABrokerSaysWhichVariablesToSet()
	{
		AreEqual(0, (await RunAsync("new", "a", "study")).Code);

		var answer = await RunAsync("import", "NVDA");

		AreEqual(1, answer.Code, "an import with no broker to ask reported success.");
		IsTrue(answer.Says("ODYSSEUS_BROKER_CONNECTOR"), $"the refusal does not name the connector variable: {answer.Error}");
		IsTrue(answer.Says("ODYSSEUS_BROKER_KEYS"), $"the refusal does not name the credentials variable: {answer.Error}");
	}

	/// <summary>
	/// A day count that is not a number is refused as one, before anything is asked of a broker.
	/// </summary>
	[TestMethod]
	public async Task ADayCountThatIsNotANumberIsRefusedAsOne()
	{
		var answer = await RunAsync("import", "NVDA", "soon");

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("not a number of days"), $"the refusal is not about the argument that is wrong: {answer.Error}");
		IsTrue(answer.Says("odysseus import NVDA 90 5m"), $"the refusal does not show the form: {answer.Error}");
	}

	/// <summary>
	/// A day count that asks for no history at all is refused rather than sent.
	/// </summary>
	/// <remarks>
	/// Nothing here is arithmetic for its own sake: the window is measured backwards from yesterday, so a
	/// count of zero asks for an empty one and a negative count asks for a window that runs the wrong way.
	/// Neither produces a shorter history - they produce a wrong one, frozen under a manifest that says
	/// what was asked for.
	/// </remarks>
	[TestMethod]
	public async Task ADayCountThatAsksForNoHistoryIsRefused()
	{
		foreach (var days in new[] { "0", "-30" })
		{
			var answer = await RunAsync("import", "NVDA", days);

			AreEqual(1, answer.Code, $"a window of {days} days was accepted.");
			IsTrue(answer.Says("not a number of days"), $"a window of {days} days was refused for the wrong reason: {answer.Error}");
		}
	}

	/// <summary>A candle length that is not one is refused, with the lengths that are.</summary>
	[TestMethod]
	public async Task ACandleLengthThatIsNotOneIsRefusedWithTheFormsThatAre()
	{
		var answer = await RunAsync("import", "NVDA", "90", "fortnightly");

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("not a candle length"), $"the refusal is not about the candle length: {answer.Error}");
		IsTrue(answer.Says("00:05:00"), $"the refusal does not show what a candle length looks like: {answer.Error}");
	}

	/// <summary>The named lengths are the ones the help offers.</summary>
	[TestMethod]
	public async Task TheCandleLengthsTheHelpOffersAreTheOnesItTakes()
	{
		AreEqual(0, (await RunAsync("new", "a", "study")).Code);

		foreach (var frame in new[] { "1m", "5m", "15m", "1h", "1d", "00:05:00" })
		{
			var answer = await RunAsync("import", "NVDA", "30", frame);

			// It gets as far as wanting a broker, which is the step after the arguments were understood.
			IsTrue(answer.Says("No broker"), $"'{frame}' was not accepted as a candle length: {answer.Error}");
		}
	}

	/// <summary>
	/// The commands the switch dispatches, read off the source.
	/// </summary>
	/// <returns>The command words.</returns>
	/// <remarks>
	/// Read from the file rather than listed here, so that a command added tomorrow is held to the same
	/// promise without anybody remembering to add it to a list in a test.
	/// </remarks>
	private static IReadOnlyCollection<string> Dispatched()
	{
		var source = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Odysseus.Cli", "Program.cs"));
		var from = source.IndexOf("args[0] switch", StringComparison.Ordinal);

		IsTrue(from > 0, "the command switch was not found, so this test is asserting nothing.");

		var block = source[from..];

		block = block[..block.IndexOf("_ =>", StringComparison.Ordinal)];

		return [.. Regex.Matches(block, "\"([a-z]+)\" =>").Select(m => m.Groups[1].Value)];
	}

	/// <summary>
	/// The commands the help table offers, read off what it printed.
	/// </summary>
	/// <param name="help">What the help wrote to standard output.</param>
	/// <returns>The first word of each row's first column.</returns>
	private static IReadOnlyCollection<string> Listed(string help)
		=> [.. help
			.Split('\n')
			.Select(line => line.Split('│'))
			.Where(cells => cells.Length > 2)
			.Select(cells => cells[1].Trim().Split(' ')[0])
			.Where(word => word.Length > 0)];
}
