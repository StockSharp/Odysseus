namespace StockSharp.Odysseus.Engine.Tests;

/// <summary>
/// What crosses between a session and the process that is trading, and whether it comes back the same.
/// </summary>
/// <remarks>
/// The two ends of this protocol are deployed as one build and are still two processes, so nothing here
/// is checked by the compiler. What makes it worth pinning is which fields are on it: the mode, the
/// position and the working orders are the three a person acts on, and a field that silently arrived as
/// its default would read exactly like a strategy that is flat and idle.
/// </remarks>
[TestClass]
public class RunnerProtocolTests : OdysseusTestBase
{
	private static readonly DateTime _observed = new(2026, 9, 4, 11, 30, 0, DateTimeKind.Utc);

	/// <summary>A greeting survives the crossing, mode included.</summary>
	[TestMethod]
	public async Task AGreetingCrossesUnchanged()
	{
		var hello = new RunnerHello(
			RunnerProtocol.Version,
			"engine-under-test",
			24188,
			"dep_abcdef",
			TradingModes.Live,
			"StockSharp.Example 1.2.3",
			"U1234567",
			_observed);

		var back = await RoundTripAsync(hello);

		AreEqual(hello.Protocol, back.Protocol);
		AreEqual(hello.Engine, back.Engine);
		AreEqual(hello.ProcessId, back.ProcessId);
		AreEqual(hello.DeploymentId, back.DeploymentId);
		AreEqual(TradingModes.Live, back.Mode, "the mode did not survive the crossing.");
		AreEqual(hello.Connector, back.Connector);
		AreEqual(hello.Account, back.Account);
		AreEqual(hello.StartedAt, back.StartedAt);
	}

	/// <summary>A request survives it, including the decision about the position.</summary>
	[TestMethod]
	public async Task ARequestCarriesTheDecisionAboutThePosition()
	{
		var request = new RunnerRequest("q1", RunnerCommands.Stop, "0123456789abcdef", true);

		var back = await RoundTripAsync(request);

		AreEqual(request.Id, back.Id);
		AreEqual(RunnerCommands.Stop, back.Command);
		AreEqual(request.Token, back.Token);
		IsTrue(back.ClosePosition, "a stop that was to close the position crossed as one that was not.");
	}

	/// <summary>
	/// A state survives it whole. Every number here is one somebody decides on, and a default arriving in
	/// place of a value reads as a flat, idle strategy rather than as a field that went missing.
	/// </summary>
	[TestMethod]
	public async Task AStateCrossesWholeOrNotAtAll()
	{
		var state = new RunnerState(
			_observed,
			RunnerPhases.Trading,
			TradingModes.Live,
			true,
			7,
			3,
			4,
			12.5m,
			-4m,
			2,
			"U1234567",
			_observed.AddDays(26),
			null);

		var back = await RoundTripAsync(new RunnerAnswer("q2", true, state, null, null));

		IsTrue(back.Succeeded);
		IsNull(back.Account, "an answer that carried no account brought one across.");

		AreEqual(state.ObservedAt, back.State.ObservedAt);
		AreEqual(RunnerPhases.Trading, back.State.Phase);
		AreEqual(TradingModes.Live, back.State.Mode, "the mode did not survive the crossing.");
		IsTrue(back.State.IsRunning);
		AreEqual(7, back.State.OrdersPlaced);
		AreEqual(3, back.State.Trades);
		AreEqual(12.5m, back.State.RealizedProfit);
		AreEqual(-4m, back.State.Position, "a short position crossed as something else.");
		AreEqual(2, back.State.WorkingOrders, "the working orders did not survive, and they are not the position.");
		AreEqual("U1234567", back.State.Account);
		AreEqual(state.MandateExpiresAt, back.State.MandateExpiresAt);
	}

	/// <summary>An account read through the runner's own connection crosses with it.</summary>
	[TestMethod]
	public async Task AnAccountCrossesWithTheStateItWasReadWith()
	{
		var account = new PaperAccountState(
			_observed,
			"stub",
			new("U1234567", true, false, 25_000m, "USD"),
			[new("AAPL", 10m, 190m, 1_900m, 12m)],
			[]);

		var back = await RoundTripAsync(new RunnerAnswer("q3", true, null, account, null));

		AreEqual("U1234567", back.Account.Account.Name, "the account's own name did not survive the crossing.");
		AreEqual(25_000m, back.Account.Account.BuyingPower);
		AreEqual(1, back.Account.Positions.Count);
		AreEqual("AAPL", back.Account.Positions[0].Symbol);
	}

	/// <summary>A failure crosses as a failure, with the reason and without a state that was never taken.</summary>
	[TestMethod]
	public async Task AFailureCrossesWithItsReason()
	{
		var back = await RoundTripAsync(new RunnerAnswer("q4", false, null, null, "the token does not match"));

		IsFalse(back.Succeeded);
		AreEqual("the token does not match", back.Failure);
		IsNull(back.State);
	}

	/// <summary>
	/// The pipe a session looks on is derived from the deployment, so a session that knows the deployment
	/// can find the process without having read anything the launcher wrote.
	/// </summary>
	[TestMethod]
	public void ThePipeIsDerivedFromTheDeployment()
	{
		AreEqual("odysseus-runner-dep_abcdef", RunnerProtocol.PipeOf("dep_abcdef"));

		AreNotEqual(
			RunnerProtocol.PipeOf("dep_one"),
			RunnerProtocol.PipeOf("dep_two"),
			"two deployments were given one pipe, so a stop could reach the wrong strategy.");
	}

	/// <summary>
	/// The framing is the worker's, unforked. Two copies of a length prefix are two things that can
	/// disagree, and the one place they would disagree is in production.
	/// </summary>
	[TestMethod]
	public async Task TheFramingIsTheOneTheWorkerAlreadyUses()
	{
		using var stream = new MemoryStream();

		await WorkerProtocol.WriteAsync(stream, new RunnerRequest("q5", RunnerCommands.Observe, "t", false), CancellationToken);

		stream.Position = 0;

		// Four bytes of big-endian length, then the body. Read as the worker's own reader reads it.
		var length = (stream.GetBuffer()[0] << 24) | (stream.GetBuffer()[1] << 16) |
			(stream.GetBuffer()[2] << 8) | stream.GetBuffer()[3];

		AreEqual(stream.Length - 4, length, "the frame does not announce its own body length.");
	}

	private async Task<T> RoundTripAsync<T>(T message)
		where T : class
	{
		using var stream = new MemoryStream();

		await WorkerProtocol.WriteAsync(stream, message, CancellationToken);

		stream.Position = 0;

		return await WorkerProtocol.ReadAsync<T>(stream, CancellationToken);
	}
}
