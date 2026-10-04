namespace Odysseus.Cli.Tests;

using System;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.Engine;

/// <summary>
/// What the two commands over a running deployment do, and what they refuse.
/// </summary>
/// <remarks>
/// These are the commands that act on money. Stopping decides what happens to an open position; killing
/// decides nothing about it and ends the process holding it anyway - so the arguments are read strictly,
/// the exit code says whether anything was actually reached, and the one question put to a person is put
/// through a port a test can answer.
///
/// Nothing here starts a runner. A record in the registry is what a session that did not start one has
/// to work from, and every case below is exactly that: a directory, written by somebody else, naming a
/// process that is not there any more.
/// </remarks>
// Standard output is one thing for the whole process, so these cannot run beside anything that writes.
[DoNotParallelize]
[TestClass]
public class RunnerCommandTests : CliTestBase
{
	/// <summary>A machine with nothing deployed says so rather than printing an empty table.</summary>
	[TestMethod]
	public async Task RunnersOnAQuietMachineSaysNothingIsRunning()
	{
		var answer = await RunAsync("runners");

		AreEqual(0, answer.Code, answer.Error);
		IsTrue(answer.Says("Nothing is running"), $"an empty registry printed something else: {answer.Out}");
	}

	/// <summary>A deployment this session did not start is still found, and reported as what it is.</summary>
	[TestMethod]
	public async Task RunnersFindsWhatAnotherSessionLeftBehind()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		var answer = await RunAsync("runners");

		AreEqual(0, answer.Code, answer.Error);
		IsTrue(answer.Says(deployment), $"a recorded deployment was not listed: {answer.Out}");
		IsTrue(answer.Says("Gone"), $"a runner whose process has ended was not reported as gone: {answer.Out}");
		IsTrue(answer.Says("Paper"), $"the account the runner was on was not reported: {answer.Out}");
	}

	/// <summary>The runner command without enough to act on shows both forms it takes.</summary>
	[TestMethod]
	public async Task TheRunnerCommandWithoutADeploymentShowsBothForms()
	{
		foreach (var args in new[] { new[] { "runner" }, new[] { "runner", "stop" }, new[] { "runner", "kill" } })
		{
			var answer = await RunAsync(args);

			AreEqual(1, answer.Code, $"'{string.Join(' ', args)}' reported success without naming a deployment.");
			IsTrue(answer.Says("runner stop"), $"the refusal does not show the stop form: {answer.Error}");
			IsTrue(answer.Says("runner kill"), $"the refusal does not show the kill form: {answer.Error}");
		}
	}

	/// <summary>A verb the command does not have is refused as the unknown command it is.</summary>
	[TestMethod]
	public async Task AVerbTheRunnerCommandDoesNotHaveIsRefused()
	{
		var answer = await RunAsync("runner", "restart", FreshDeployment());

		AreEqual(1, answer.Code);
		IsTrue(answer.Error.Contains("There is no command 'runner restart'", StringComparison.Ordinal),
			$"the refusal does not name what was typed: {answer.Error}");
	}

	/// <summary>
	/// Stopping something that was never there is not a success.
	/// </summary>
	/// <remarks>
	/// A person stops a deployment in a script and reads the exit code to know whether the position was
	/// dealt with. Zero here would say it had been, for a deployment nothing on this machine has ever
	/// heard of - which is most often a mistyped identifier, and always something to be told about.
	/// </remarks>
	[TestMethod]
	public async Task StoppingWhatWasNeverThereIsNotReportedAsSuccess()
	{
		var answer = await RunAsync("runner", "stop", FreshDeployment());

		AreEqual(1, answer.Code, "stopping a deployment that does not exist reported success.");
		IsTrue(answer.Says("No runner was ever recorded"), $"nothing said what was found: {answer.Out}");
	}

	/// <summary>
	/// Nor is stopping one whose process had already gone, which closed nothing.
	/// </summary>
	[TestMethod]
	public async Task StoppingARunnerThatHadAlreadyGoneIsNotReportedAsStopped()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		var answer = await RunAsync("runner", "stop", deployment, "--close");

		AreEqual(1, answer.Code, "a stop that reached nothing, and closed nothing, reported success.");
		IsTrue(answer.Says("Gone"), $"nothing said what was found instead: {answer.Out}");
	}

	/// <summary>
	/// An argument the stop does not read is refused rather than passed over.
	/// </summary>
	/// <remarks>
	/// The one that matters is a mistyped <c>--close</c>. Ignored, the position stays open while the
	/// command that was meant to close it prints a stop and exits; refused, the person types it again.
	/// </remarks>
	[TestMethod]
	public async Task AnArgumentTheStopDoesNotReadIsRefusedRatherThanIgnored()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		foreach (var mistyped in new[] { "--closs", "--live", "close" })
		{
			var answer = await RunAsync("runner", "stop", deployment, mistyped);

			AreEqual(1, answer.Code, $"'{mistyped}' was passed over.");
			IsTrue(answer.Says($"'{mistyped}' is not an argument"), $"the refusal does not name it: {answer.Error}");
			IsFalse(answer.Says("The deployment's own record"), $"'{mistyped}' was passed over and the stop went ahead.");
		}
	}

	/// <summary>The flag it does read is still read.</summary>
	[TestMethod]
	public async Task TheOnlyFlagTheStopTakesIsStillTaken()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		var answer = await RunAsync("runner", "stop", deployment, "--close");

		IsFalse(answer.Says("is not an argument"), $"--close was refused by the command that documents it: {answer.Error}");
	}

	/// <summary>Killing something with no record says where it looked for one.</summary>
	[TestMethod]
	public async Task KillingSomethingWithNoRecordSaysWhereItLooked()
	{
		var answer = await RunAsync("runner", "kill", FreshDeployment());

		AreEqual(1, answer.Code);
		IsTrue(answer.Says(Workspace.RunnerRegistry.Directory),
			$"the refusal does not say which registry was read: {answer.Error}");
	}

	/// <summary>
	/// Killing takes no other argument, so nothing typed beside it is quietly acted on.
	/// </summary>
	[TestMethod]
	public async Task KillingTakesNoOtherArgument()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		var answer = await RunAsync("runner", "kill", deployment, "--force");

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("'--force' is not an argument"), $"the refusal does not name it: {answer.Error}");
		AreEqual(0, Terminal.Questions.Count, "a command that refused its arguments still asked a person something.");
	}

	/// <summary>
	/// With nobody at the terminal it is refused - after saying what would have been left open.
	/// </summary>
	[TestMethod]
	public async Task KillingWithNobodyAtTheTerminalIsRefused()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		Terminal.IsInteractive = false;

		var answer = await RunAsync("runner", "kill", deployment);

		AreEqual(1, answer.Code, "a kill with nobody to confirm it reported success.");
		IsTrue(answer.Says("needs a person at a terminal"), $"the refusal does not say why: {answer.Error}");
		IsTrue(answer.Says("Killing decides nothing about the position"),
			$"nothing was said about what would have been left open: {answer.Out}");
		AreEqual(0, Terminal.Questions.Count, "a terminal with nobody at it was asked a question anyway.");
	}

	/// <summary>What is about to be left open is printed before the question, not after it.</summary>
	[TestMethod]
	public async Task WhatWillBeLeftOpenIsPrintedBeforeTheQuestion()
	{
		var deployment = FreshDeployment();
		var record = Record(deployment, Dead());

		Terminal.Types("no");

		var answer = await RunAsync("runner", "kill", deployment);

		AreEqual(1, answer.Code);
		AreEqual(1, Terminal.Questions.Count, "the person was not asked exactly once.");

		IsTrue(Terminal.Questions[0].Contains($"{record.ProcessId}", StringComparison.Ordinal),
			$"the question does not say which process is about to end: {Terminal.Questions[0]}");

		var before = Terminal.SeenBeforeAsking[0];

		IsTrue(before.Contains("AAPL", StringComparison.Ordinal),
			$"what the runner was trading was not printed before the question: {before}");
		IsTrue(before.Contains("its working orders stay working", StringComparison.Ordinal),
			$"what happens to its orders was not said before the question: {before}");
	}

	/// <summary>Typing something else kills nothing, and says so.</summary>
	[TestMethod]
	public async Task TypingSomethingElseKillsNothing()
	{
		var deployment = FreshDeployment();

		Record(deployment, Dead());

		Terminal.Types(deployment[..^1]);

		var answer = await RunAsync("runner", "kill", deployment);

		AreEqual(1, answer.Code);
		IsTrue(answer.Says("Nothing was killed"), $"the refusal does not say that nothing happened: {answer.Error}");

		// It never reached the process, which is what the other refusal would have said. Nearly right is
		// not right: the identifier is compared exactly, so almost typing it stops before the kill.
		IsFalse(answer.Says("There is no process"), "an identifier that was not the one typed reached the kill anyway.");
	}

	/// <summary>
	/// Typing it back reaches the process, and a process that has since gone is reported as gone.
	/// </summary>
	/// <remarks>
	/// The far side of the same gate. What is typed is trimmed before it is compared - a terminal adds
	/// nothing to a line, but a paste does - and what follows a match is the kill itself, which here
	/// finds the number is no longer anybody's.
	/// </remarks>
	[TestMethod]
	public async Task TypingTheIdentifierBackReachesTheProcess()
	{
		var deployment = FreshDeployment();
		var record = Record(deployment, Dead());

		Terminal.Types("  " + deployment + "  ");

		var answer = await RunAsync("runner", "kill", deployment);

		AreEqual(1, answer.Code, "a process that is not there was reported as ended.");
		IsTrue(answer.Says($"There is no process {record.ProcessId}"),
			$"the identifier was typed back and the kill was not attempted: {answer.Everything}");
	}

	/// <summary>A deployment identifier of this test's own.</summary>
	/// <returns>The identifier.</returns>
	private static string FreshDeployment() => $"dep_cli_{Guid.NewGuid():n}";

	/// <summary>
	/// A process number no process holds.
	/// </summary>
	/// <returns>The number.</returns>
	/// <remarks>
	/// A record naming one reads as a runner whose process has ended, which is the state every case here
	/// wants. It is far above any number an operating system hands out rather than merely one that is
	/// free at the moment it is asked for: a number that was free when the record was written and taken
	/// by the time the kill reaches it is a test that ends somebody else's process.
	/// </remarks>
	private static int Dead()
	{
		const int number = 1_073_741_823;

		IsNull(SystemProcessProbe.Instance.StartedAt(number), "a number no operating system issues is in use.");

		return number;
	}

	/// <summary>
	/// Writes the record another session would have left behind.
	/// </summary>
	/// <param name="deploymentId">Deployment the record is for.</param>
	/// <param name="processId">Process it names.</param>
	/// <returns>The record, as it was written.</returns>
	private RunnerRecord Record(string deploymentId, int processId)
	{
		var started = DateTime.UtcNow.AddMinutes(-5);

		var record = new RunnerRecord(
			RunnerHome.Schema,
			RunnerProtocol.Version,
			"engine-under-test",
			deploymentId,
			"prj_" + deploymentId[^8..],
			"cnd_" + deploymentId[^8..],
			TradingModes.Paper,
			"AAPL",
			10m,
			"StockSharp.Example",
			"1.0.0",
			"U1234567",
			RunnerProtocol.PipeOf(deploymentId),
			processId,
			started,
			SystemProcessProbe.Instance.BootedAt,
			started);

		Workspace.RunnerRegistry.Home(deploymentId).WriteRecord(record);

		return record;
	}
}
