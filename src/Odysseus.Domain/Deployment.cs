namespace StockSharp.Odysseus.Domain;

/// <summary>
/// What became of a deployment.
/// </summary>
/// <remarks>
/// Written down by name, so a value added between two others changes nothing about rows already stored.
/// </remarks>
public enum DeploymentStatuses
{
	/// <summary>The strategy is running against the broker right now.</summary>
	Running,

	/// <summary>
	/// A row was written and the process that trades it has not greeted yet.
	/// </summary>
	/// <remarks>
	/// The row is written before the runner is started, so that a crash in between leaves a record of what
	/// was being attempted rather than a process nothing knows about - or a process nobody recorded. Found
	/// in this state afterwards it means one of exactly two things, and which of them is answered by
	/// looking for the runner rather than by reading the row.
	/// </remarks>
	Starting,

	/// <summary>It was stopped on purpose.</summary>
	Stopped,

	/// <summary>
	/// The process that was trading it is gone. Nothing was stopped in an orderly fashion, so whatever the
	/// strategy had open at the broker was left exactly as it stood.
	/// </summary>
	Interrupted,

	/// <summary>It stopped itself because something went wrong.</summary>
	Failed,
}

/// <summary>
/// One run of a measured candidate against a live account.
/// </summary>
/// <param name="Id">Identifier of the deployment.</param>
/// <param name="Candidate">Candidate that was deployed.</param>
/// <param name="Symbol">Symbol it trades.</param>
/// <param name="Volume">Size of one position, fixed for the life of the deployment.</param>
/// <param name="Mode">
/// Which account it trades on. Recorded so that a deployment can be listed for what it is without
/// contacting anything, and so that a live one that cannot be reached still says it is live.
/// </param>
/// <param name="ProcessId">
/// The process trading it, so a person can find it without this server, or zero when none was recorded.
/// </param>
/// <param name="Status">Where it stands.</param>
/// <param name="StartedAt">When it started, in UTC.</param>
/// <param name="StoppedAt">When it stopped, in UTC, or null while it runs.</param>
/// <param name="OrdersPlaced">Orders the strategy sent.</param>
/// <param name="Trades">Positions it opened and closed.</param>
/// <param name="SessionDays">Distinct trading days it received candles for.</param>
/// <param name="RealizedProfit">What those came to, as the broker reports it.</param>
/// <param name="Position">What it was holding when last looked at.</param>
/// <param name="LastObservedAt">
/// When the numbers above were last read from a live session, in UTC, or null while none ever was.
/// </param>
/// <param name="Note">Why it stopped, when that needs saying.</param>
/// <remarks>
/// A deployment outlives the server that started it. It runs in a process of its own, and this row is the
/// record of it rather than the thing itself: a deployment found in
/// <see cref="DeploymentStatuses.Running"/> when a session starts may well still be trading, and whether
/// it is is answered by looking for the process, never by reading this.
///
/// Which means the row can be behind. Everything counted here was true when somebody last watched, and
/// <see cref="LastObservedAt"/> says when that was; a runner nobody has spoken to since has gone on
/// trading without any of these numbers moving.
/// </remarks>
public sealed record Deployment(
	DeploymentId Id,
	CandidateId Candidate,
	string Symbol,
	decimal Volume,
	TradingModes Mode,
	int ProcessId,
	DeploymentStatuses Status,
	DateTime StartedAt,
	DateTime? StoppedAt,
	int OrdersPlaced,
	int Trades,
	int SessionDays,
	decimal RealizedProfit,
	decimal Position,
	DateTime? LastObservedAt,
	string Note);
