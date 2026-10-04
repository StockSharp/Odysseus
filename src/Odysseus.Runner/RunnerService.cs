namespace Odysseus.Runner;

using System.Globalization;

using Odysseus.Domain;

/// <summary>
/// One deployment, for as long as it trades.
/// </summary>
/// <remarks>
/// The whole of a runner. It asks whoever started it for the phrase its mandate is written around, loads
/// a connector, proves to itself that the account it is about to reach is the one it is permitted to
/// reach, starts the strategy, and then answers questions about it until somebody stops it or it stops
/// itself.
///
/// Every check the launcher could have made is made again here, from the files this process read, on
/// this process's own clock. That is not distrust of the launcher for its own sake: the launcher is a
/// different program that may be a different version, may have been killed halfway through, and - in the
/// one case that matters - may have been asked for something by an agent. The process that is going to
/// place the orders is the only one whose refusal cannot be routed around.
///
/// It never closes a position of its own accord. A stop is a decision about the position and carries the
/// answer with it; a death, a signal and an expiry are not decisions about the position, and none of
/// them becomes one by default.
/// </remarks>
internal sealed class RunnerService : IAsyncDisposable
{
	private readonly RunnerHome _home;
	private readonly RunnerPlan _plan;
	private readonly TradingMandate _mandate;
	private readonly LiveMandateConfirmation _confirmation;
	private readonly IConnectorFactory _connectors;
	private readonly string _engine;
	private readonly IClock _clock;
	private readonly Lock _sync = new();
	private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private IPaperSession _session;
	private IPaperAccount _account;
	private RunnerPhases _phase = RunnerPhases.Starting;
	private RunnerRecord _record;
	private string _accountName = string.Empty;
	private string _connector = string.Empty;
	private string _error;
	private bool _stopping;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="home">The directory this runner owns.</param>
	/// <param name="plan">What it was told to run.</param>
	/// <param name="mandate">Which account it may reach, as this process was configured at start-up.</param>
	/// <param name="confirmation">
	/// How the phrase in that mandate is demanded back, which is what makes holding the file something
	/// other than the whole of the permission.
	/// </param>
	/// <param name="connectors">How a connector is loaded.</param>
	/// <param name="engine">Which build of the trading platform this process carries.</param>
	/// <param name="clock">Source of the current moment.</param>
	public RunnerService(
		RunnerHome home,
		RunnerPlan plan,
		TradingMandate mandate,
		LiveMandateConfirmation confirmation,
		IConnectorFactory connectors,
		string engine,
		IClock clock)
	{
		_home = home ?? throw new ArgumentNullException(nameof(home));
		_plan = plan ?? throw new ArgumentNullException(nameof(plan));
		_mandate = mandate ?? throw new ArgumentNullException(nameof(mandate));
		_confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
		_connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
		_engine = engine ?? string.Empty;
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>The token a client has to present before this runner will answer it.</summary>
	public string Token => _plan.Token ?? string.Empty;

	/// <summary>Completes when the strategy is no longer running, however it ended.</summary>
	public Task Finished => _finished.Task;

	/// <summary>
	/// What this runner says about itself before it is asked anything.
	/// </summary>
	/// <returns>The greeting.</returns>
	public RunnerHello Greeting()
	{
		using (_sync.EnterScope())
		{
			return new(
				RunnerProtocol.Version,
				_engine,
				Environment.ProcessId,
				_plan.DeploymentId,
				_mandate.Mode,
				_connector,
				_accountName,
				_record?.StartedAt ?? _clock.UtcNow);
		}
	}

	/// <summary>
	/// Writes down how to find this process, before it connects to anything.
	/// </summary>
	/// <param name="probe">What the machine says about a process number and its last restart.</param>
	/// <remarks>
	/// Written first so that a launcher which dies between the spawn and the handshake leaves a
	/// discoverable process rather than an orphan. It says Paper or Live from the moment it exists,
	/// because the state most in need of a person is a live runner nobody can reach.
	/// </remarks>
	public void Announce(IProcessProbe probe)
	{
		ArgumentNullException.ThrowIfNull(probe);

		var started = _clock.UtcNow;

		var record = new RunnerRecord(
			RunnerHome.Schema,
			RunnerProtocol.Version,
			_engine,
			_plan.DeploymentId,
			_plan.ProjectId,
			_plan.CandidateId,
			_mandate.Mode,
			_plan.Symbol,
			_plan.Volume,
			_plan.Connector?.PackageId ?? string.Empty,
			_plan.Connector?.PackageVersion ?? string.Empty,
			string.Empty,
			_plan.Pipe,
			Environment.ProcessId,
			probe.StartedAt(Environment.ProcessId) ?? started,
			probe.BootedAt,
			started);

		using (_sync.EnterScope())
			_record = record;

		_home.WriteRecord(record);

		Journal(RunnerPhases.Starting, $"Starting {_plan.ClassName} on {_plan.Symbol} - {_mandate.Mode}.");
	}

	/// <summary>
	/// Demands the mandate's phrase, loads the connector, proves the account is the permitted one, and
	/// starts the strategy.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <exception cref="LiveMandateInvalidException">
	/// What this process is permitted does not cover what it was asked to do, or nobody returned the
	/// phrase that permission is written around.
	/// </exception>
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		try
		{
			// Before anything is downloaded, because none of these answers depend on the connector and
			// being refused after a download is a slower way to learn the same thing.
			AssertPermitted();

			// And after them, because nobody should be made to type anything for a mandate that does not
			// cover this deployment in the first place. This is the gate the file cannot open by itself:
			// whoever starts a live runner says the phrase back, or nothing starts.
			//
			// The token goes with the question. An interrupt at the terminal is somebody saying they are
			// not going to answer it, and without it here that interrupt would leave this process waiting
			// on a line nobody is typing, with a project held and a strategy nobody started.
			_confirmation.Assert(_mandate, cancellationToken);

			var binding = await _connectors.OpenAsync(_plan.Connector, cancellationToken);

			_connector = $"{binding.Connector.PackageId} {binding.Connector.PackageVersion}";

			// Which build was loaded has already been settled inside the factory, against the mandate this
			// process was born with. What is left is the question only the broker can answer: which account
			// these credentials actually reach.
			await AssertAccountAsync(binding, cancellationToken);

			var session = await binding.Trader.StartAsync(
				new(
					await File.ReadAllBytesAsync(_home.AssemblyFile, cancellationToken),
					_plan.ClassName,
					_plan.Parameters,
					_plan.Symbol,
					_plan.TimeFrame,
					_plan.Volume),
				cancellationToken);

			// The account was checked on a connection of its own, and the session may have landed on
			// another one the same credentials reach. The account that trades is the one that counts.
			if (_mandate.IsLive && !string.Equals(session.Account, _mandate.Account, StringComparison.OrdinalIgnoreCase))
			{
				await session.StopAsync(closePosition: false, cancellationToken);

				throw new LiveMandateInvalidException(
					$"The mandate at '{_mandate.Origin}' is for account '{_mandate.Account}' and the strategy was " +
					$"started on '{session.Account}'. It was stopped before it traded.");
			}

			bool stoppedWhileStarting;

			using (_sync.EnterScope())
			{
				// A stop that arrived while this was starting has already been answered as a stop, so the
				// strategy that came up in the meantime is ended rather than left trading.
				stoppedWhileStarting = _stopping || _finished.Task.IsCompleted;

				if (!stoppedWhileStarting)
				{
					_session = session;
					_account = binding.Account;
					_accountName = session.Account ?? string.Empty;
					_phase = RunnerPhases.Trading;
				}
			}

			if (stoppedWhileStarting)
			{
				await session.StopAsync(closePosition: false, cancellationToken);
				await session.DisposeAsync();

				return;
			}

			Rewrite();
			Journal(RunnerPhases.Trading, $"Trading {_plan.Symbol} at {Written(_plan.Volume)} per position on '{_accountName}'.");
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			Fail(error.Message);
			throw;
		}
	}

	/// <summary>
	/// What the strategy has done.
	/// </summary>
	/// <returns>The state.</returns>
	public RunnerState Observe()
	{
		using (_sync.EnterScope())
		{
			var session = _session;

			return new(
				_clock.UtcNow,
				_phase,
				_mandate.Mode,
				session is { IsRunning: true },
				session?.OrdersPlaced ?? 0,
				session?.Trades ?? 0,
				session?.SessionDays ?? 0,
				session?.RealizedProfit ?? 0m,
				session?.Position ?? 0m,
				session?.WorkingOrders ?? 0,
				_accountName,
				_mandate.IsLive ? _mandate.ExpiresAt : null,
				_error);
		}
	}

	/// <summary>
	/// Reads the account this runner is trading on.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The account, its holdings and its live orders.</returns>
	public ValueTask<PaperAccountState> AccountAsync(CancellationToken cancellationToken)
	{
		IPaperAccount account;

		using (_sync.EnterScope())
			account = _account;

		return account is null
			? throw new InvalidOperationException(
				"This runner has not reached the broker yet, so there is no account to read.")
			: account.ReadAsync(cancellationToken);
	}

	/// <summary>
	/// Stops the strategy because somebody asked.
	/// </summary>
	/// <param name="closePosition">Whether to close what it is holding.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The state it ended in.</returns>
	/// <remarks>
	/// Idempotent, because several clients may be attached and the second one to ask is not making a
	/// mistake. The first decides what happens to the position; a later one is answered with the state
	/// and told that a stop was already under way, rather than being allowed to place a second closing
	/// order against a position the first one has already flattened.
	/// </remarks>
	public async Task<RunnerState> StopAsync(bool closePosition, CancellationToken cancellationToken)
	{
		IPaperSession session;

		using (_sync.EnterScope())
		{
			if (_stopping)
				return Observe();

			_stopping = true;
			_phase = RunnerPhases.Stopping;
			session = _session;
		}

		var what = closePosition
			? "Stopped, closing what it held."
			: $"Stopped, leaving a position of {Written(session?.Position ?? 0m)} open at the broker.";

		await EndAsync(session, closePosition, what, cancellationToken);

		return Observe();
	}

	/// <summary>
	/// Stops the strategy because this process is going away.
	/// </summary>
	/// <param name="why">What is ending it.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <remarks>
	/// It never closes the position, and that is the rule the whole design rests on: closing on a signal
	/// is trading on nobody's behalf. Whoever sent the signal did not say what to do about the position,
	/// so nothing is done about it and the journal says exactly that.
	///
	/// A deployment that has already finished is not ended a second time. A runner that was refused its
	/// mandate goes on to exit, and the exit would otherwise write "stopped, nothing was left open" over
	/// the reason it never started - which is the line a later session reads, and the one that has to
	/// say why nobody is trading.
	/// </remarks>
	public async Task EndAsync(string why, CancellationToken cancellationToken)
	{
		IPaperSession session;

		using (_sync.EnterScope())
		{
			if (_stopping || _finished.Task.IsCompleted)
				return;

			_stopping = true;
			_phase = RunnerPhases.Stopping;
			session = _session;
		}

		await EndAsync(
			session,
			closePosition: false,
			$"{why} The position of {Written(session?.Position ?? 0m)} was left open: nothing about it was " +
			"asked, so nothing about it was decided.",
			cancellationToken);
	}

	/// <summary>
	/// Watches the strategy until it is no longer trading, writing down what it is holding as it goes.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <remarks>
	/// The heartbeat is what a session reads when this process is gone. Without it the last thing anybody
	/// knows about a runner that crashed is whatever the last observation happened to catch, which is
	/// usually the moment before the interesting one.
	/// </remarks>
	public async Task WatchAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			bool stopping;

			// Watched until it has finished rather than until somebody asked it to stop: a stop that fails
			// leaves the strategy trading, and it is still this process's to watch and to stop again.
			using (_sync.EnterScope())
			{
				if (_finished.Task.IsCompleted)
					return;

				stopping = _stopping;
			}

			if (!stopping)
			{
				var state = Observe();

				if (!state.IsRunning)
				{
					// Its own report rather than an inference from its absence, which is why it is recorded
					// as a failure rather than as a stop: nobody asked it to end.
					Fail(_session?.Error ?? "The strategy stopped by itself.");

					return;
				}

				if (_mandate.IsExpired(_clock.UtcNow))
				{
					await EndAsync(
						$"The mandate authorising this runner expired at {_mandate.ExpiresAt:O}.", cancellationToken);

					continue;
				}

				Journal(state.Phase, "Still trading.");
			}

			try
			{
				await Task.Delay(_plan.Heartbeat, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		IPaperSession session;

		using (_sync.EnterScope())
			session = _session;

		if (session is not null)
			await session.DisposeAsync();
	}

	private static string Written(decimal value)
		=> value.ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// Refuses to start what this process is not permitted to do, before anything is downloaded.
	/// </summary>
	/// <exception cref="LiveMandateInvalidException">The plan asks for something the mandate does not cover.</exception>
	private void AssertPermitted()
	{
		if (!_mandate.IsLive)
			return;

		if (_mandate.IsExpired(_clock.UtcNow))
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{_mandate.Origin}' expired at {_mandate.ExpiresAt:O}. Nothing was started.");
		}

		if (!_mandate.Allows(_plan.Symbol))
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{_mandate.Origin}' permits {string.Join(", ", _mandate.Symbols)} and this " +
				$"deployment trades {_plan.Symbol}. A mandate is for the instruments somebody meant.");
		}

		if (!string.Equals(_plan.Connector?.PackageId, _mandate.PackageId, StringComparison.OrdinalIgnoreCase))
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{_mandate.Origin}' is for '{_mandate.PackageId}' and this deployment was told " +
				$"to load '{_plan.Connector?.PackageId}'. Nothing was started.");
		}
	}

	/// <summary>
	/// Checks the account the broker actually reports against the one the mandate names.
	/// </summary>
	/// <param name="binding">The connector that was loaded.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <exception cref="LiveMandateInvalidException">The broker answered for another account, or the position would be too large.</exception>
	/// <remarks>
	/// The check that catches a credential file pointed at the wrong account, which nothing else can:
	/// every other check here is about what was asked for, and this is the only one about what answered.
	/// It runs before the strategy is started, on a connection of its own, so a mismatch costs nothing
	/// but the reading.
	/// </remarks>
	private async Task AssertAccountAsync(BrokerBinding binding, CancellationToken cancellationToken)
	{
		if (!_mandate.IsLive)
			return;

		var state = await binding.Account.ReadAsync(cancellationToken);

		if (!string.Equals(state.Account.Name, _mandate.Account, StringComparison.OrdinalIgnoreCase))
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{_mandate.Origin}' is for account '{_mandate.Account}' and the broker " +
				$"answered for '{state.Account.Name}'. The credentials this process was given reach a " +
				"different account than the one somebody authorised, so nothing was started.");
		}

		if (await binding.Quotes.GetPriceAsync(_plan.Symbol, cancellationToken) is not { } price)
		{
			throw new LiveMandateInvalidException(
				$"The broker quoted no current price for {_plan.Symbol}, so this process cannot show that a " +
				$"position of {Written(_plan.Volume)} stays under the {Written(_mandate.MaxPositionNotional)} " +
				"the mandate caps it at. A cap that cannot be checked is not a cap, so nothing was started.");
		}

		var notional = _plan.Volume * price;

		if (notional > _mandate.MaxPositionNotional)
		{
			throw new LiveMandateInvalidException(
				$"One position of {Written(_plan.Volume)} {_plan.Symbol} at {Written(price)} comes to " +
				$"{Written(notional)}, and the mandate at '{_mandate.Origin}' caps it at " +
				$"{Written(_mandate.MaxPositionNotional)}. Nothing was started.");
		}
	}

	private async Task EndAsync(
		IPaperSession session,
		bool closePosition,
		string what,
		CancellationToken cancellationToken)
	{
		try
		{
			if (session is not null)
				await session.StopAsync(closePosition, cancellationToken);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// It was asked to stop and something went wrong doing it, which is not the same as it having
			// stopped: the position may still be open and orders may still be working. Reported as a
			// failure with the reason, and the process stays to watch the strategy and take another stop,
			// because a runner that exited here would leave the position with nobody attending it.
			var why = $"Stopping failed: {error.Message}";

			using (_sync.EnterScope())
			{
				_phase = RunnerPhases.Failed;
				_error = why;
				_stopping = false;
			}

			Journal(RunnerPhases.Failed, why);

			return;
		}

		using (_sync.EnterScope())
			_phase = RunnerPhases.Stopped;

		Journal(RunnerPhases.Stopped, what);

		_finished.TrySetResult();
	}

	private void Fail(string why)
	{
		using (_sync.EnterScope())
		{
			_phase = RunnerPhases.Failed;
			_error = why;
		}

		Journal(RunnerPhases.Failed, why);

		_finished.TrySetResult();
	}

	/// <summary>
	/// Writes the record again, once the broker has said which account answered.
	/// </summary>
	private void Rewrite()
	{
		RunnerRecord record;

		using (_sync.EnterScope())
		{
			if (_record is null)
				return;

			record = _record with { Account = _accountName };

			_record = record;
		}

		_home.WriteRecord(record);
	}

	private void Journal(RunnerPhases phase, string what)
	{
		var state = Observe();

		_home.Append(new(
			_clock.UtcNow,
			phase,
			_mandate.Mode,
			state.Position,
			state.WorkingOrders,
			state.Trades,
			state.RealizedProfit,
			what));
	}
}
