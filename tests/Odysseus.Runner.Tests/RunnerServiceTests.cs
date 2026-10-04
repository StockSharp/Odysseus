namespace Odysseus.Runner.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Engine;
using Odysseus.Runner;
using Odysseus.TestKit;

/// <summary>
/// One deployment, for as long as it trades, without a broker anywhere near it.
/// </summary>
/// <remarks>
/// Two things are worth pinning here and they pull in opposite directions. The first is that a live
/// runner refuses everything its mandate does not cover, and refuses it before it has placed an order -
/// so each refusal is arranged with a stand-in broker rather than waited for on a real account. The
/// second is that nothing ever closes a position except a stop that was asked to: a signal does not, an
/// expiry does not, and a process going away does not.
/// </remarks>
[TestClass]
public partial class RunnerServiceTests : OdysseusTestBase
{
	private static readonly DateTime _now = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

	private string _root;

	/// <summary>Makes a home of its own.</summary>
	[TestInitialize]
	public void CreateHome()
		=> _root = Path.Combine(Path.GetTempPath(), "odysseus-runner-service", Guid.NewGuid().ToString("n"));

	/// <summary>Removes it.</summary>
	[TestCleanup]
	public void DeleteHome()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// A runner writes down how to find it before it connects to anything, so a launcher that dies in
	/// between leaves a discoverable process rather than an orphan.
	/// </summary>
	[TestMethod]
	public void ARunnerAnnouncesItselfBeforeItReachesABroker()
	{
		var home = Home();
		var broker = new Broker();

		var service = new RunnerService(
			home, Plan(), TradingMandate.Paper, Confirmed(TradingMandate.Paper), broker, "test-engine", new Clock());

		service.Announce(new Probe());

		var record = home.ReadRecord();

		IsNotNull(record, "nothing was written down about a runner that was about to trade.");
		AreEqual("dep_one", record.DeploymentId);
		AreEqual(TradingModes.Paper, record.Mode);
		AreEqual("test-engine", record.Engine);

		IsFalse(broker.WasOpened, "the broker was reached before the runner had said where to find it.");
	}

	/// <summary>A paper runner starts, and says what account it ended up on.</summary>
	[TestMethod]
	public async Task APaperRunnerStartsAndNamesItsAccount()
	{
		var service = Service(TradingMandate.Paper, out var broker, out _);

		await service.StartAsync(CancellationToken);

		var state = service.Observe();

		AreEqual(RunnerPhases.Trading, state.Phase);
		AreEqual(TradingModes.Paper, state.Mode);
		IsTrue(state.IsRunning);
		AreEqual(broker.Trader.Session.Account, state.Account,
			"the state names an account other than the one the broker reported.");
		IsNull(state.MandateExpiresAt, "a paper runner reported a permission that expires.");

		IsTrue(broker.WasOpened);
	}

	/// <summary>
	/// A live runner refuses an instrument its mandate does not name, and refuses it before anything is
	/// downloaded or connected.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerRefusesAnInstrumentItsMandateDoesNotName()
	{
		var service = Service(Mandate() with { Symbols = ["MSFT"] }, out var broker, out var home);

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsTrue(refusal.Message.Contains("AAPL", StringComparison.Ordinal),
			$"the refusal does not say what it was asked to trade: {refusal.Message}");

		IsFalse(broker.WasOpened, "a refusal that could be made from the files still went and loaded a connector.");
		AreEqual(RunnerPhases.Failed, service.Observe().Phase);

		IsNotNull(home.LastEntry(), "nothing was written down about a runner that refused to start.");
	}

	/// <summary>
	/// A live runner refuses when the broker answers for an account other than the one it was authorised
	/// for. It is the only check that catches a credential file pointed at the wrong account.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerRefusesAnAccountItWasNotAuthorisedFor()
	{
		var service = Service(Mandate(), out var broker, out _, account: "U-somebodyelse");

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsTrue(refusal.Message.Contains("U-somebodyelse", StringComparison.Ordinal),
			$"the refusal does not say which account answered: {refusal.Message}");

		IsFalse(broker.Trader.WasStarted, "a strategy was started on an account nobody authorised.");
	}

	/// <summary>
	/// A live runner refuses when the account it ends up trading on is not the one it was authorised for,
	/// even though the account it checked was. The check and the trading go through separate connections,
	/// and a broker with several accounts can hand the strategy a different one from the one read back.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerRefusesWhenItTradesOnAnAccountOtherThanTheOneItChecked()
	{
		var service = Service(Mandate(), out var broker, out _, sessionAccount: "U-other");

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsTrue(refusal.Message.Contains("U-other", StringComparison.Ordinal),
			$"the refusal does not say which account the strategy was put on: {refusal.Message}");

		IsTrue(!broker.Trader.WasStarted || broker.Trader.Session.WasStopped,
			"a strategy was left trading on an account nobody authorised.");

		AreEqual(RunnerPhases.Failed, service.Observe().Phase);
	}

	/// <summary>
	/// A stop that arrives while the runner is still starting wins. Answering it as stopped and then
	/// going on to trade would leave a strategy running that everybody has been told is not.
	/// </summary>
	[TestMethod]
	public async Task AStopDuringStartLeavesNothingTrading()
	{
		var service = Service(TradingMandate.Paper, out var broker, out _);

		broker.Trader.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

		var starting = service.StartAsync(CancellationToken);

		await broker.Trader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

		var answered = await service.StopAsync(closePosition: false, CancellationToken);

		broker.Trader.Gate.SetResult();

		try
		{
			await starting;
		}
		catch (Exception error) when (error is not AssertFailedException)
		{
			// A start that gives up because it was stopped is an acceptable answer.
		}

		AreEqual(RunnerPhases.Stopped, answered.Phase, "the stop was not answered as a stop.");

		IsTrue(!broker.Trader.WasStarted || broker.Trader.Session.WasStopped,
			"the strategy went on to trade after the stop had been answered.");

		AreNotEqual(RunnerPhases.Trading, service.Observe().Phase,
			"the runner reports trading after it answered a stop.");
	}

	/// <summary>A live runner refuses a position larger than the one it was authorised to hold.</summary>
	[TestMethod]
	public async Task ALiveRunnerRefusesAPositionLargerThanItsCap()
	{
		// Ten at a hundred is a thousand, and the mandate here permits five hundred.
		var service = Service(Mandate() with { MaxPositionNotional = 500m }, out var broker, out _);

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsTrue(refusal.Message.Contains("500", StringComparison.Ordinal),
			$"the refusal does not say what the cap was: {refusal.Message}");

		IsFalse(broker.Trader.WasStarted, "a position larger than the authorised one was opened.");
	}

	/// <summary>
	/// A cap that cannot be checked is not a cap. A broker with no recent price for the instrument means
	/// the runner cannot show the position stays under the ceiling, so it starts nothing.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerRefusesWhenItCannotCheckItsOwnCap()
	{
		var service = Service(Mandate(), out var broker, out _, prices: false);

		await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsFalse(broker.Trader.WasStarted, "a runner traded under a ceiling it could not check.");
	}

	/// <summary>
	/// The cap is checked against the price the broker quotes now, not against the last bar of the past
	/// week: a price that has since moved would let a position past the ceiling it was authorised under.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerChecksItsCapAgainstThePriceQuotedNow()
	{
		// History closes at a hundred, which would be a thousand; the broker quotes six hundred now, which
		// is six thousand against a cap of five.
		var service = Service(Mandate(), out var broker, out _, quote: 600m);

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsTrue(refusal.Message.Contains("600", StringComparison.Ordinal),
			$"the refusal does not say which price it was checked at: {refusal.Message}");

		IsFalse(broker.Trader.WasStarted, "a position past the cap at the current price was opened.");
	}

	/// <summary>A live runner that is within its mandate starts, and every report says it is live.</summary>
	[TestMethod]
	public async Task ALiveRunnerSaysSoInEveryReport()
	{
		var mandate = Mandate();
		var home = Home();
		var broker = new Broker();

		var service = new RunnerService(
			home, Plan(), mandate, Confirmed(mandate), broker, "test-engine", new Clock());

		service.Announce(new Probe());

		await service.StartAsync(CancellationToken);

		AreEqual(TradingModes.Live, service.Observe().Mode);
		AreEqual<DateTime?>(mandate.ExpiresAt, service.Observe().MandateExpiresAt);
		AreEqual(TradingModes.Live, service.Greeting().Mode);
		AreEqual(TradingModes.Live, home.ReadRecord().Mode, "a record nobody can reach would not say it is live.");
		AreEqual(TradingModes.Live, home.LastEntry().Mode);
	}

	/// <summary>
	/// A live runner whose phrase was not typed back starts nothing, holds nothing, and writes down that
	/// it was refused. Holding the mandate file is not the permission: saying what is in it is.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerRefusedForItsPhraseHoldsNothingAndSaysWhy()
	{
		var home = Home();
		var broker = new Broker();
		var mandate = Mandate();

		var service = new RunnerService(
			home,
			Plan(),
			mandate,
			new(new Terminal("trade real money"), string.Empty),
			broker,
			"test-engine",
			new Clock());

		service.Announce(new Probe());

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsTrue(refusal.Message.Contains("phrase", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say what went unconfirmed: {refusal.Message}");

		IsFalse(broker.WasOpened, "a runner nobody confirmed went and loaded a connector.");
		IsFalse(broker.Trader.WasStarted, "a strategy was started on a mandate nobody confirmed.");

		var state = service.Observe();

		AreEqual(RunnerPhases.Failed, state.Phase);
		AreEqual(0m, state.Position, "a runner that never started is holding something.");
		AreEqual(0, state.WorkingOrders);

		var last = home.LastEntry();

		IsNotNull(last, "nothing was written down about a runner that was refused for its phrase.");
		AreEqual(RunnerPhases.Failed, last.Phase);
		AreEqual(TradingModes.Live, last.Mode, "the journal does not say which kind of account was refused.");
		AreEqual(0m, last.Position);

		AreEqual(TradingModes.Live, home.ReadRecord().Mode,
			"a refused live runner left a record that reads like a paper one.");
	}

	/// <summary>
	/// An interrupt while the phrase is being asked for starts nothing, reaches no broker, and writes
	/// down that nobody confirmed it. It is the one answer a person can give without typing, and it has
	/// to end the wait: a runner still standing at that question holds a project, answers nothing and
	/// looks from outside exactly like one that is trading.
	/// </summary>
	[TestMethod]
	public async Task AnInterruptWhileThePhraseIsBeingAskedStartsNothing()
	{
		var home = Home();
		var broker = new Broker();
		var mandate = Mandate();

		// What the runner's own console handler cancels when the interrupt arrives, and what it passes
		// to the strategy it is starting: one and the same, which is what carries the interrupt into the
		// question.
		using var stopping = new CancellationTokenSource();

		var service = new RunnerService(
			home,
			Plan(),
			mandate,
			new(new Interrupted(stopping, mandate.Phrase), string.Empty),
			broker,
			"test-engine",
			new Clock());

		service.Announce(new Probe());

		var refusal = await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(stopping.Token));

		IsTrue(refusal.Message.Contains("Nobody answered", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say that nobody answered the question: {refusal.Message}");

		IsFalse(broker.WasOpened, "a runner nobody confirmed went and loaded a connector.");
		IsFalse(broker.Trader.WasStarted, "an interrupted question started a strategy on a real account.");

		var state = service.Observe();

		AreEqual(RunnerPhases.Failed, state.Phase);
		AreEqual(TradingModes.Live, state.Mode, "an interrupted live runner reported itself as a paper one.");
		AreEqual(0m, state.Position, "a runner that never started is holding something.");

		var last = home.LastEntry();

		IsNotNull(last, "nothing was written down about a runner that was interrupted at its phrase.");
		AreEqual(RunnerPhases.Failed, last.Phase);

		IsTrue(last.What.Contains("Nobody answered", StringComparison.OrdinalIgnoreCase),
			$"the journal does not say what went unconfirmed: {last.What}");
	}

	/// <summary>
	/// The phrase is asked for after the checks this process can make on its own, so nobody is made to
	/// type anything for a mandate that does not cover this deployment in the first place.
	/// </summary>
	[TestMethod]
	public async Task ALiveRunnerAsksForItsPhraseOnlyAfterTheChecksItCanMakeAlone()
	{
		var terminal = new Terminal("trade real money on U-live until the thirtieth");

		var service = new RunnerService(
			Home(),
			Plan(),
			Mandate() with { Symbols = ["MSFT"] },
			new(terminal, string.Empty),
			new Broker(),
			"test-engine",
			new Clock());

		service.Announce(new Probe());

		await ThrowsAsync<LiveMandateInvalidException>(() => service.StartAsync(CancellationToken));

		IsFalse(terminal.WasAsked,
			"somebody was asked to type a phrase for a mandate that does not cover this deployment.");
	}

	/// <summary>A paper runner asks nobody anything: there is no real account to confirm.</summary>
	[TestMethod]
	public async Task APaperRunnerAsksNobodyForAnything()
	{
		var terminal = new Terminal("whatever");

		var service = new RunnerService(
			Home(),
			Plan(),
			TradingMandate.Paper,
			new(terminal, string.Empty),
			new Broker(),
			"test-engine",
			new Clock());

		service.Announce(new Probe());

		await service.StartAsync(CancellationToken);

		IsFalse(terminal.WasAsked, "a paper runner stopped to ask a person for a phrase.");
		AreEqual(RunnerPhases.Trading, service.Observe().Phase);
	}

	/// <summary>A stop that was asked to close closes, and a stop that was not leaves the position open.</summary>
	[TestMethod]
	public async Task AStopCarriesTheDecisionAboutThePosition()
	{
		var service = Service(TradingMandate.Paper, out var broker, out _);

		await service.StartAsync(CancellationToken);

		await service.StopAsync(closePosition: true, CancellationToken);

		IsTrue(broker.Trader.Session.WasStopped);
		IsTrue(broker.Trader.Session.WasAskedToClose, "a stop that was to close the position did not carry that.");
		AreEqual(RunnerPhases.Stopped, service.Observe().Phase);
	}

	/// <summary>
	/// A stop is idempotent. Several clients may be attached, and the second one to ask is not making a
	/// mistake - but it must not be allowed to place a second closing order against a position the first
	/// one has already flattened.
	/// </summary>
	[TestMethod]
	public async Task AsecondStopDoesNotTradeAgain()
	{
		var service = Service(TradingMandate.Paper, out var broker, out _);

		await service.StartAsync(CancellationToken);

		await service.StopAsync(closePosition: false, CancellationToken);
		await service.StopAsync(closePosition: true, CancellationToken);

		AreEqual(1, broker.Trader.Session.Stops, "the runner was stopped twice, and the second one traded.");
		IsFalse(broker.Trader.Session.WasAskedToClose, "a later stop overrode the decision the first one made.");
	}

	/// <summary>
	/// Ending on a signal never closes the position. Whoever sent the signal did not say what to do about
	/// it, so nothing is done about it, and the journal says exactly that.
	/// </summary>
	[TestMethod]
	public async Task EndingOnASignalNeverClosesThePosition()
	{
		var service = Service(TradingMandate.Paper, out var broker, out var home);

		await service.StartAsync(CancellationToken);

		await service.EndAsync("Stopped by an interrupt at the terminal.", CancellationToken);

		IsTrue(broker.Trader.Session.WasStopped);
		IsFalse(broker.Trader.Session.WasAskedToClose, "a signal closed a position nobody asked about.");

		IsTrue(home.LastEntry().What.Contains("left open", StringComparison.OrdinalIgnoreCase),
			$"the journal does not say the position was left open: {home.LastEntry().What}");
	}

	/// <summary>
	/// A strategy that stopped by itself is recorded as having failed rather than as having been stopped,
	/// with its own reason: nobody asked it to end.
	/// </summary>
	[TestMethod]
	public async Task AStrategyThatStoppedItselfIsAFailureRatherThanAStop()
	{
		var service = Service(TradingMandate.Paper, out var broker, out var home);

		await service.StartAsync(CancellationToken);

		broker.Trader.Session.IsRunning = false;
		broker.Trader.Session.Error = "the venue rejected every order";

		await service.WatchAsync(CancellationToken);

		AreEqual(RunnerPhases.Failed, service.Observe().Phase);
		AreEqual("the venue rejected every order", service.Observe().Error);
		AreEqual("the venue rejected every order", home.LastEntry().What);
	}

	/// <summary>
	/// A stop that failed halfway is not a stop. Recording it as one would say the position was dealt
	/// with when it may still be open and orders may still be working.
	/// </summary>
	[TestMethod]
	public async Task AStopThatFailedIsNotRecordedAsAStop()
	{
		var service = Service(TradingMandate.Paper, out var broker, out _);

		await service.StartAsync(CancellationToken);

		broker.Trader.Session.RefuseToStop = true;

		await service.StopAsync(closePosition: true, CancellationToken);

		AreEqual(RunnerPhases.Failed, service.Observe().Phase,
			"a stop that threw halfway through was recorded as a deployment that stopped.");
	}

	/// <summary>
	/// A home with the assembly already in it, because the launcher writes it before the runner starts and
	/// the runner reads it rather than being handed it.
	/// </summary>
	/// <returns>The home.</returns>
	private RunnerHome Home()
	{
		var home = new RunnerHome(Path.Combine(_root, "dep_one"), "dep_one");

		home.Create();

		File.WriteAllBytes(home.AssemblyFile, [1, 2, 3]);

		return home;
	}

	private RunnerService Service(
		TradingMandate mandate,
		out Broker broker,
		out RunnerHome home,
		string account = "U-live",
		bool prices = true,
		string sessionAccount = null,
		decimal quote = 100m)
	{
		home = Home();
		broker = new(account, prices, sessionAccount, quote);

		var service = new RunnerService(
			home, Plan(), mandate, Confirmed(mandate), broker, "test-engine", new Clock());

		service.Announce(new Probe());

		return service;
	}

	/// <summary>
	/// A person at a terminal who types the phrase the mandate is written around, which is what every
	/// test that is not about the phrase itself needs to have happened.
	/// </summary>
	/// <param name="mandate">The permission being exercised.</param>
	/// <returns>The confirmation.</returns>
	private static LiveMandateConfirmation Confirmed(TradingMandate mandate)
		=> new(new Terminal(mandate.Phrase), string.Empty);

	private static RunnerPlan Plan()
		=> new(
			RunnerHome.Schema,
			"dep_one",
			"prj_one",
			"cnd_one",
			"Generated",
			"strategy.dll",
			"AAPL",
			TimeSpan.FromMinutes(5),
			10m,
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			new("StockSharp.Example", "1.2.3", "Example.Adapter", null),
			Path.Combine(Path.GetTempPath(), "odysseus-connectors"),
			["https://example.invalid"],
			["StockSharp."],
			RunnerProtocol.PipeOf("dep_one"),
			"0123456789abcdef0123456789abcdef",
			TimeSpan.FromMilliseconds(10));

	private static TradingMandate Mandate()
		=> new(
			TradingModes.Live,
			"trade real money on U-live until the thirtieth",
			"U-live",
			"StockSharp.Example",
			"1.2.3",
			"Example.Adapter",
			["AAPL"],
			5_000m,
			_now.AddDays(26),
			"/etc/odysseus/mandate.json");

	/// <summary>A person at a terminal who types whatever the test needs them to have typed.</summary>
	private sealed class Terminal(string types) : IOperatorTerminal
	{
		public bool WasAsked { get; private set; }

		public bool IsInteractive => true;

		public string Ask(string question, CancellationToken cancellationToken)
		{
			WasAsked = true;

			return cancellationToken.IsCancellationRequested ? null : types;
		}
	}

	/// <summary>
	/// A person at a terminal who presses the interrupt instead of typing.
	/// </summary>
	/// <remarks>
	/// The interrupt arrives while the question is standing, which is what Ctrl+C at a runner's phrase
	/// is: the console handler stops the deployment, the wait for a line ends with it, and nothing was
	/// typed. A real terminal reports that the same way - as nothing - so this does too, and would keep
	/// on reporting the phrase if the question had not been given up on.
	/// </remarks>
	private sealed class Interrupted(CancellationTokenSource stopping, string types) : IOperatorTerminal
	{
		public bool IsInteractive => true;

		public string Ask(string question, CancellationToken cancellationToken)
		{
			stopping.Cancel();

			return cancellationToken.IsCancellationRequested ? null : types;
		}
	}

	/// <summary>A clock that does not depend on when the test ran.</summary>
	private sealed class Clock : IClock
	{
		public DateTime UtcNow => _now;
	}

	/// <summary>A machine whose answers are arranged rather than waited for.</summary>
	private sealed class Probe : IProcessProbe
	{
		public DateTime BootedAt => _now.AddDays(-3);

		public DateTime? StartedAt(int processId) => _now.AddMinutes(-1);
	}

	/// <summary>A connector that never downloads anything and never reaches a venue.</summary>
	private sealed class Broker(string account = "U-live", bool prices = true, string sessionAccount = null, decimal quote = 100m) : IConnectorFactory
	{
		public bool WasOpened { get; private set; }

		public Trader Trader { get; } = new(sessionAccount ?? account);

		public IReadOnlyList<string> Allowed => ["StockSharp."];

		public IReadOnlyList<string> Sources => ["https://example.invalid"];

		public ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
			=> ValueTask.FromResult(Description);

		public ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken)
		{
			WasOpened = true;

			return ValueTask.FromResult(new BrokerBinding(
				Description, new History(prices), new Quotes(prices ? quote : null), Trader, new Account(account), null));
		}

		public ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException("No storage server here.");

		public ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult<IReadOnlyList<ConnectorDescription>>([Description]);

		private static ConnectorDescription Description { get; } = new(
			"StockSharp.Example", "1.2.3", "sha", "Example.Adapter", "Example",
			[], "key-secret", true, false, [], "connector:stocksharp.example@00000000");
	}

	/// <summary>History that answers with one price, or with nothing at all.</summary>
	private sealed class History(bool prices) : IHistorySource
	{
		public string SourceName => "test";

		public Task<IReadOnlyList<Candle>> GetBarsAsync(
			string symbol,
			TimeSpan timeFrame,
			DateTime from,
			DateTime to,
			CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<Candle>>(
				prices ? [new(from, 100m, 101m, 99m, 100m, 1_000m)] : []);
	}

	/// <summary>A broker that quotes one price, or has no price at all.</summary>
	private sealed class Quotes(decimal? price) : IQuoteSource
	{
		public Task<decimal?> GetPriceAsync(string symbol, CancellationToken cancellationToken)
			=> Task.FromResult(price);
	}

	/// <summary>An account that answers for whichever name it was told to.</summary>
	private sealed class Account(string name) : IPaperAccount
	{
		public ValueTask<PaperAccountState> ReadAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult(new PaperAccountState(
				_now, "test", new(name, true, false, 25_000m, "USD"), [], []));
	}

	/// <summary>A broker that accepts everything and reports whatever it is told to.</summary>
	private sealed class Trader(string account) : IPaperTrader
	{
		public string Name => "stand-in";

		public bool WasStarted { get; private set; }

		public Session Session { get; } = new(account);

		/// <summary>When set, starting waits on it, so a test can act while the start is under way.</summary>
		public TaskCompletionSource Gate { get; set; }

		/// <summary>Completes once a start has reached the broker.</summary>
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<IPaperSession> StartAsync(PaperRequest request, CancellationToken cancellationToken)
		{
			Entered.TrySetResult();

			if (Gate is not null)
				await Gate.Task.WaitAsync(cancellationToken);

			WasStarted = true;

			return Session;
		}
	}

	/// <summary>A strategy that does whatever the test needs it to have done.</summary>
	private sealed class Session(string account) : IPaperSession
	{
		public bool IsRunning { get; set; } = true;

		public int OrdersPlaced => 3;

		public int Trades => 1;

		public int SessionDays => 1;

		public decimal RealizedProfit => 4m;

		public decimal Position => 10m;

		public int WorkingOrders => 1;

		public string Account => account;

		public string Error { get; set; }

		public bool RefuseToStop { get; set; }

		public bool WasStopped { get; private set; }

		public bool WasAskedToClose { get; private set; }

		public int Stops { get; private set; }

		public Task StopAsync(bool closePosition, CancellationToken cancellationToken)
		{
			if (RefuseToStop)
				throw new InvalidOperationException("the venue would not take the cancellation");

			Stops++;
			WasStopped = true;
			WasAskedToClose = closePosition;
			IsRunning = false;

			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync() => default;
	}
}
