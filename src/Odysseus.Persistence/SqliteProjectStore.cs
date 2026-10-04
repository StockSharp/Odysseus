namespace Odysseus.Persistence;

using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;

/// <summary>
/// Projects and their audit trails, one SQLite database per project.
/// </summary>
/// <remarks>
/// A project owns its folder and its database, so it can be zipped, carried to another machine and
/// opened there without the rest of the installation. That is also why the listing walks the folders
/// instead of consulting a central index: an index would be a second place to keep the truth, and the
/// two would eventually disagree.
///
/// Everything held in that database is served from here rather than from a class per table. A second
/// class would mean a second connection to the same file, and two writers to one SQLite file is a
/// locking problem invented for the sake of a smaller class.
/// </remarks>
public sealed class SqliteProjectStore : IProjectStore, IAuditLog, ICandidateStore, IRunStore, IEvaluationStore,
	IDeploymentStore, IDisposable
{
	/// <summary>Schema the store writes. Recorded in every database it creates.</summary>
	public const int SchemaVersion = 2;

	private const string DatabaseFileName = "odysseus.db";

	// Metrics and judgements are kept as JSON in one column each: they are nested records that nothing
	// queries into, and a table per block would be a schema that has to change every time a metric does.
	// The converter is the same one the file stores use, so a typed identifier survives the round trip
	// instead of coming back as the default and losing the candidate it pointed at.
	private static readonly JsonSerializerOptions _json = new()
	{
		Converters = { new TypedIdJsonConverter() },
	};

	private const string DeploymentColumns =
		"Id, Candidate, Symbol, Volume, Status, StartedAt, StoppedAt, OrdersPlaced, Trades, " +
		"RealizedProfit, Position, LastObservedAt, Note, Mode, ProcessId, SessionDays";

	private const string RunColumns =
		"Id, Candidate, Dataset, Slice, Window, Symbol, Scenario, Fingerprint, Status, Metrics, Trades, " +
		"Equity, BarsProcessed, StartedAt, FinishedAt, Error, Diagnosis, Parameters";

	private const string CandidateColumns =
		"Id, Spec, Status, ClassName, SourceHash, AssemblyHash, Source, Assembly, TranslatorVersion, CreatedAt, UpdatedAt";

	private readonly string _root;
	private readonly Lock _sync = new();
	private readonly Dictionary<string, ProjectDatabase> _databases = new(StringComparer.Ordinal);

	/// <summary>
	/// Creates the store.
	/// </summary>
	/// <param name="root">Directory the project folders live in. Created if it does not exist.</param>
	public SqliteProjectStore(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		_root = Path.GetFullPath(root);

		Directory.CreateDirectory(_root);
	}

	/// <inheritdoc />
	public async ValueTask CreateAsync(ResearchProject project, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(project);

		var database = Connect(project.Id);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				INSERT INTO Projects (Id, Name, Status, CreatedAt, UpdatedAt, Dataset,
				                      MaxBacktests, MaxCandidates, MaxWallClockTicks,
				                      ClaimedBacktests, ClaimedCandidates, ClaimedWallClockTicks)
				VALUES ($id, $name, $status, $createdAt, $updatedAt, $dataset,
				        $maxBacktests, $maxCandidates, $maxWallClock,
				        $claimedBacktests, $claimedCandidates, $claimedWallClock);
				""";

			Bind(command, project);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpdateAsync(ResearchProject project, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(project);

		if (!Exists(project.Id))
			throw new ProjectNotFoundException(project.Id);

		var database = Connect(project.Id);

		var affected = await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			// The allowance is not written here. It is changed only by claiming and charging, which happen
			// where the number is kept; writing it back from whatever this caller happened to read would
			// undo a claim made by anyone else in between - which is the race those methods exist to close.
			command.CommandText =
				"""
				UPDATE Projects
				SET Name = $name, Status = $status, UpdatedAt = $updatedAt, Dataset = $dataset
				WHERE Id = $id;
				""";

			Bind(command, project);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);

		if (affected == 0)
			throw new ProjectNotFoundException(project.Id);
	}

	/// <inheritdoc />
	public async ValueTask<ResearchProject> OpenAsync(ProjectId id, CancellationToken cancellationToken)
	{
		if (!Exists(id))
			throw new ProjectNotFoundException(id);

		return await ReadProjectAsync(id, cancellationToken) ?? throw new ProjectNotFoundException(id);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<ResearchProject>> ListAsync(CancellationToken cancellationToken)
	{
		var projects = new List<ResearchProject>();

		foreach (var folder in Directory.EnumerateDirectories(_root))
		{
			if (!ProjectId.TryParse(Path.GetFileName(folder), out var id) ||
				!File.Exists(Path.Combine(folder, DatabaseFileName)))
				continue;

			var project = await ReadProjectAsync(id, cancellationToken);

			if (project is not null)
				projects.Add(project);
		}

		return [.. projects.OrderByDescending(p => p.UpdatedAt)];
	}

	/// <inheritdoc />
	public async ValueTask<AuditEvent> AppendAsync(
		ProjectId project,
		AuditEventTypes type,
		Actors actor,
		string detail,
		string payloadHash,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(detail);
		ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);

		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var occurredAt = DateTime.UtcNow;
		var database = Connect(project);

		var sequence = await database.RunAsync(async (connection, token) =>
		{
			// The position is assigned by the database rather than read and incremented here, so two
			// callers appending at once cannot be handed the same one.
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				INSERT INTO AuditEvents (Type, Actor, OccurredAt, PayloadHash, Detail)
				VALUES ($type, $actor, $occurredAt, $payloadHash, $detail)
				RETURNING Sequence;
				""";

			command.Parameters.AddWithValue("$type", type.ToString());
			command.Parameters.AddWithValue("$actor", actor.ToString());
			command.Parameters.AddWithValue("$occurredAt", Format(occurredAt));
			command.Parameters.AddWithValue("$payloadHash", payloadHash);
			command.Parameters.AddWithValue("$detail", detail);

			return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
		}, cancellationToken);

		return new()
		{
			Sequence = sequence,
			Type = type,
			Actor = actor,
			OccurredAt = occurredAt,
			PayloadHash = payloadHash,
			Detail = detail,
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<AuditEvent>> ReadAsync(ProjectId project, CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync<IReadOnlyList<AuditEvent>>(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"SELECT Sequence, Type, Actor, OccurredAt, PayloadHash, Detail FROM AuditEvents ORDER BY Sequence;";

			var events = new List<AuditEvent>();

			await using var reader = await command.ExecuteReaderAsync(token);

			while (await reader.ReadAsync(token))
			{
				events.Add(new()
				{
					Sequence = reader.GetInt64(0),
					Type = Enum.Parse<AuditEventTypes>(reader.GetString(1)),
					Actor = Enum.Parse<Actors>(reader.GetString(2)),
					OccurredAt = ParseMoment(reader.GetString(3)),
					PayloadHash = reader.GetString(4),
					Detail = reader.GetString(5),
				});
			}

			return events;
		}, cancellationToken);
	}

	/// <summary>
	/// Reads the schema version a project's database was written with.
	/// </summary>
	/// <param name="project">Project to inspect.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The schema version.</returns>
	/// <exception cref="ProjectNotFoundException">The project does not exist.</exception>
	public async ValueTask<int> GetSchemaVersionAsync(ProjectId project, CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = "SELECT Version FROM SchemaInfo;";

			return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		using (_sync.EnterScope())
		{
			foreach (var database in _databases.Values)
				database.Dispose();

			_databases.Clear();
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryClaimAsync(
		ProjectId project,
		int backtests,
		int candidates,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(backtests);
		ArgumentOutOfRangeException.ThrowIfNegative(candidates);

		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		var granted = await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			// One statement, so the figure is read and written where it lives. Two callers arriving
			// together are serialised by the database rather than racing over a copy each of them holds.
			command.CommandText =
				"""
				UPDATE Projects
				SET ClaimedBacktests = ClaimedBacktests + $backtests,
				    ClaimedCandidates = ClaimedCandidates + $candidates
				WHERE Id = $id
				  AND ClaimedBacktests + $backtests <= MaxBacktests
				  AND ClaimedCandidates + $candidates <= MaxCandidates
				  AND ClaimedWallClockTicks < MaxWallClockTicks;
				""";

			command.Parameters.AddWithValue("$id", project.Value);
			command.Parameters.AddWithValue("$backtests", backtests);
			command.Parameters.AddWithValue("$candidates", candidates);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);

		return granted == 1;
	}

	/// <inheritdoc />
	public async ValueTask ReleaseAsync(
		ProjectId project,
		int backtests,
		int candidates,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(backtests);
		ArgumentOutOfRangeException.ThrowIfNegative(candidates);

		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			// Floored at zero in the statement rather than checked around it, for the same reason the
			// claim is ceilinged in the statement: two of these arriving together must not read the same
			// figure and both write the same one back.
			command.CommandText =
				"""
				UPDATE Projects
				SET ClaimedBacktests = MAX(0, ClaimedBacktests - $backtests),
				    ClaimedCandidates = MAX(0, ClaimedCandidates - $candidates)
				WHERE Id = $id;
				""";

			command.Parameters.AddWithValue("$id", project.Value);
			command.Parameters.AddWithValue("$backtests", backtests);
			command.Parameters.AddWithValue("$candidates", candidates);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ChargeTimeAsync(ProjectId project, TimeSpan elapsed, CancellationToken cancellationToken)
	{
		if (elapsed <= TimeSpan.Zero)
			return;

		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				UPDATE Projects
				SET ClaimedWallClockTicks = ClaimedWallClockTicks + $ticks
				WHERE Id = $id;
				""";

			command.Parameters.AddWithValue("$id", project.Value);
			command.Parameters.AddWithValue("$ticks", elapsed.Ticks);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask AddAsync(ProjectId project, Candidate candidate, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(candidate);

		var database = Connect(project);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				INSERT INTO Candidates (Id, Spec, Status, ClassName, SourceHash, AssemblyHash,
				                        Source, Assembly, TranslatorVersion, CreatedAt, UpdatedAt)
				VALUES ($id, $spec, $status, $className, $sourceHash, $assemblyHash,
				        $source, $assembly, $translator, $createdAt, $updatedAt);
				""";

			Bind(command, candidate);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpdateAsync(ProjectId project, Candidate candidate, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(candidate);

		var database = Connect(project);

		var affected = await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				UPDATE Candidates
				SET Spec = $spec, Status = $status, ClassName = $className,
				    SourceHash = $sourceHash, AssemblyHash = $assemblyHash,
				    Source = $source, Assembly = $assembly,
				    TranslatorVersion = $translator, CreatedAt = $createdAt, UpdatedAt = $updatedAt
				WHERE Id = $id;
				""";

			Bind(command, candidate);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);

		if (affected == 0)
			throw new CandidateNotFoundException(candidate.Id);
	}

	/// <inheritdoc />
	public async ValueTask<Candidate> GetAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
		=> await ReadCandidateAsync(project, "Id = $value", candidate.Value, cancellationToken)
			?? throw new CandidateNotFoundException(candidate);

	/// <inheritdoc />
	public ValueTask<Candidate> FindBySourceAsync(
		ProjectId project,
		string sourceHash,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);

		return ReadCandidateAsync(project, "SourceHash = $value", sourceHash, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<Candidate>> ListAsync(ProjectId project, CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = $"SELECT {CandidateColumns} FROM Candidates ORDER BY Sequence;";

			await using var reader = await command.ExecuteReaderAsync(token);

			var candidates = new List<Candidate>();

			while (await reader.ReadAsync(token))
				candidates.Add(ReadCandidate(reader));

			return (IReadOnlyList<Candidate>)candidates;
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask AddAsync(ProjectId project, RunResult run, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(run);

		var database = Connect(project);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				INSERT INTO Runs (Id, Candidate, Dataset, Slice, Window, Symbol, Scenario, Fingerprint,
				                  Status, Metrics, Trades, Equity, BarsProcessed, StartedAt, FinishedAt, Error,
				                  Diagnosis, Parameters)
				VALUES ($id, $candidate, $dataset, $slice, $window, $symbol, $scenario, $fingerprint,
				        $status, $metrics, $trades, $equity, $bars, $startedAt, $finishedAt, $error,
				        $diagnosis, $parameters);
				""";

			command.Parameters.AddWithValue("$id", run.Id.Value);
			command.Parameters.AddWithValue("$candidate", run.Candidate.Value);
			command.Parameters.AddWithValue("$dataset", run.Dataset.Value);
			command.Parameters.AddWithValue("$slice", run.Slice.ToString());
			command.Parameters.AddWithValue("$window", run.Window);
			command.Parameters.AddWithValue("$symbol", run.Symbol);
			command.Parameters.AddWithValue("$scenario", run.Scenario);
			command.Parameters.AddWithValue("$fingerprint", run.Fingerprint);
			command.Parameters.AddWithValue("$status", run.Status.ToString());
			command.Parameters.AddWithValue("$metrics", run.Metrics is null ? DBNull.Value : JsonSerializer.Serialize(run.Metrics, _json));
			command.Parameters.AddWithValue("$trades", run.Trades.Value);
			command.Parameters.AddWithValue("$equity", run.Equity.Value);
			command.Parameters.AddWithValue("$bars", run.BarsProcessed);
			command.Parameters.AddWithValue("$startedAt", Format(run.StartedAt));
			command.Parameters.AddWithValue("$finishedAt", Format(run.FinishedAt));
			command.Parameters.AddWithValue("$error", (object)run.Error ?? DBNull.Value);
			command.Parameters.AddWithValue("$diagnosis", (object)run.Diagnosis ?? DBNull.Value);
			command.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(run.Parameters ?? new Dictionary<string, decimal>(), _json));

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<RunResult> GetAsync(ProjectId project, RunId run, CancellationToken cancellationToken)
		=> await ReadRunAsync(project, "Id = $value", run.Value, cancellationToken)
			?? throw new RunNotFoundException(run);

	/// <inheritdoc />
	public ValueTask<RunResult> FindAsync(ProjectId project, string fingerprint, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

		return ReadRunAsync(project, "Fingerprint = $value AND Status = 'Completed'", fingerprint, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RunResult>> ListAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = candidate.IsEmpty
				? $"SELECT {RunColumns} FROM Runs ORDER BY Sequence;"
				: $"SELECT {RunColumns} FROM Runs WHERE Candidate = $candidate ORDER BY Sequence;";

			if (!candidate.IsEmpty)
				command.Parameters.AddWithValue("$candidate", candidate.Value);

			await using var reader = await command.ExecuteReaderAsync(token);

			var runs = new List<RunResult>();

			while (await reader.ReadAsync(token))
				runs.Add(ReadRun(reader));

			return (IReadOnlyList<RunResult>)runs;
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask AddAsync(ProjectId project, Deployment deployment, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(deployment);

		var database = Connect(project);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				INSERT INTO Deployments (Id, Candidate, Symbol, Volume, Status, StartedAt, StoppedAt,
				                         OrdersPlaced, Trades, RealizedProfit, Position, LastObservedAt,
				                         Note, Mode, ProcessId, SessionDays)
				VALUES ($id, $candidate, $symbol, $volume, $status, $startedAt, $stoppedAt,
				        $orders, $trades, $profit, $position, $observedAt, $note, $mode, $processId, $sessionDays);
				""";

			Describe(command, deployment);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpdateAsync(ProjectId project, Deployment deployment, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(deployment);

		var database = Connect(project);

		var changed = await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				UPDATE Deployments
				SET Candidate = $candidate, Symbol = $symbol, Volume = $volume, Status = $status,
				    StartedAt = $startedAt, StoppedAt = $stoppedAt, OrdersPlaced = $orders,
				    Trades = $trades, RealizedProfit = $profit, Position = $position,
				    LastObservedAt = $observedAt, Note = $note, Mode = $mode, ProcessId = $processId,
				    SessionDays = $sessionDays
				WHERE Id = $id;
				""";

			Describe(command, deployment);

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);

		if (changed == 0)
			throw new DeploymentNotFoundException(deployment.Id);
	}

	/// <inheritdoc />
	public async ValueTask<Deployment> GetAsync(ProjectId project, DeploymentId deployment, CancellationToken cancellationToken)
	{
		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = $"SELECT {DeploymentColumns} FROM Deployments WHERE Id = $id;";
			command.Parameters.AddWithValue("$id", deployment.Value);

			await using var reader = await command.ExecuteReaderAsync(token);

			if (!await reader.ReadAsync(token))
				throw new DeploymentNotFoundException(deployment);

			return ReadDeployment(reader);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask AddAsync(
		ProjectId project,
		Measurement measurement,
		IReadOnlyList<RunId> runs,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(measurement);
		ArgumentNullException.ThrowIfNull(runs);

		var database = Connect(project);

		await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				INSERT INTO Measurements (Candidate, Content, Runs, MeasuredAt)
				VALUES ($candidate, $content, $runs, $at);
				""";

			command.Parameters.AddWithValue("$candidate", measurement.Candidate.Value);
			command.Parameters.AddWithValue("$content", JsonSerializer.Serialize(measurement, _json));
			command.Parameters.AddWithValue("$runs", string.Join(",", runs.Select(r => r.Value)));
			command.Parameters.AddWithValue("$at", Format(measurement.MeasuredAt));

			return await command.ExecuteNonQueryAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<Measurement>> ListMeasurementsAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				SELECT Content FROM Measurements
				WHERE Candidate = $candidate
				ORDER BY Sequence;
				""";

			command.Parameters.AddWithValue("$candidate", candidate.Value);

			await using var reader = await command.ExecuteReaderAsync(token);

			var found = new List<Measurement>();

			while (await reader.ReadAsync(token))
				found.Add(JsonSerializer.Deserialize<Measurement>(reader.GetString(0), _json));

			return (IReadOnlyList<Measurement>)found;
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<int> CountAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = "SELECT COUNT(*) FROM Measurements WHERE Candidate = $candidate;";
			command.Parameters.AddWithValue("$candidate", candidate.Value);

			return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<(Measurement Measurement, IReadOnlyList<RunId> Runs)> FindAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			// Newest first: a candidate measured twice was measured the second time on what was known then.
			command.CommandText =
				"""
				SELECT Content, Runs FROM Measurements
				WHERE Candidate = $candidate
				ORDER BY Sequence DESC
				LIMIT 1;
				""";

			command.Parameters.AddWithValue("$candidate", candidate.Value);

			await using var reader = await command.ExecuteReaderAsync(token);

			if (!await reader.ReadAsync(token))
				return (null, (IReadOnlyList<RunId>)[]);

			var measurement = JsonSerializer.Deserialize<Measurement>(reader.GetString(0), _json);

			var runs = reader.GetString(1)
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(RunId.Parse)
				.ToArray();

			return (measurement, (IReadOnlyList<RunId>)runs);
		}, cancellationToken);
	}

	private static void Bind(DbCommand command, ResearchProject project)
	{
		Add(command, "$id", project.Id.Value);
		Add(command, "$name", project.Name);
		Add(command, "$status", project.Status.ToString());
		Add(command, "$createdAt", Format(project.CreatedAt));
		Add(command, "$updatedAt", Format(project.UpdatedAt));
		Add(command, "$dataset", project.Dataset.IsEmpty ? DBNull.Value : project.Dataset.Value);
		Add(command, "$maxBacktests", project.Budget.MaxBacktests);
		Add(command, "$maxCandidates", project.Budget.MaxCandidates);
		Add(command, "$maxWallClock", project.Budget.MaxWallClock.Ticks);
		Add(command, "$claimedBacktests", project.Budget.ClaimedBacktests);
		Add(command, "$claimedWallClock", project.Budget.ClaimedWallClock.Ticks);
		Add(command, "$claimedCandidates", project.Budget.ClaimedCandidates);
	}

	private static void Add(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();

		parameter.ParameterName = name;
		parameter.Value = value;

		command.Parameters.Add(parameter);
	}

	// Round-trip format, so a moment read back is the moment written and its kind is never guessed from
	// the machine the project happens to be opened on.
	private static string Format(DateTime moment)
		=> moment.ToString("O", CultureInfo.InvariantCulture);

	private static DateTime ParseMoment(string text)
		=> DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

	// Text rather than a floating column: a price or a size read back has to be the one written, and
	// SQLite has no decimal of its own to keep it in.
	private static string Format(decimal number)
		=> number.ToString(CultureInfo.InvariantCulture);

	private static decimal ParseNumber(string text)
		=> decimal.Parse(text, CultureInfo.InvariantCulture);

	private static Candidate ReadCandidate(DbDataReader reader)
		=> new(
			CandidateId.Parse(reader.GetString(0)),
			SpecId.Parse(reader.GetString(1)),
			Enum.Parse<CandidateStatuses>(reader.GetString(2)),
			reader.GetString(3),
			reader.GetString(4),
			reader.GetString(5),
			ArtifactId.Parse(reader.GetString(6)),
			ArtifactId.Parse(reader.GetString(7)),
			reader.GetString(8),
			ParseMoment(reader.GetString(9)),
			ParseMoment(reader.GetString(10)));

	private static void Bind(SqliteCommand command, Candidate candidate)
	{
		command.Parameters.AddWithValue("$id", candidate.Id.Value);
		command.Parameters.AddWithValue("$spec", candidate.Spec.Value);
		command.Parameters.AddWithValue("$status", candidate.Status.ToString());
		command.Parameters.AddWithValue("$className", candidate.ClassName);
		command.Parameters.AddWithValue("$sourceHash", candidate.SourceHash);
		command.Parameters.AddWithValue("$assemblyHash", candidate.AssemblyHash);
		command.Parameters.AddWithValue("$source", candidate.Source.Value);
		command.Parameters.AddWithValue("$assembly", candidate.Assembly.Value);
		command.Parameters.AddWithValue("$translator", candidate.TranslatorVersion);
		command.Parameters.AddWithValue("$createdAt", Format(candidate.CreatedAt));
		command.Parameters.AddWithValue("$updatedAt", Format(candidate.UpdatedAt));
	}

	private static void Describe(SqliteCommand command, Deployment deployment)
	{
		command.Parameters.AddWithValue("$id", deployment.Id.Value);
		command.Parameters.AddWithValue("$candidate", deployment.Candidate.Value);
		command.Parameters.AddWithValue("$symbol", deployment.Symbol);
		command.Parameters.AddWithValue("$volume", Format(deployment.Volume));
		command.Parameters.AddWithValue("$status", deployment.Status.ToString());
		command.Parameters.AddWithValue("$startedAt", Format(deployment.StartedAt));
		command.Parameters.AddWithValue("$stoppedAt", deployment.StoppedAt is { } stopped ? Format(stopped) : DBNull.Value);
		command.Parameters.AddWithValue("$orders", deployment.OrdersPlaced);
		command.Parameters.AddWithValue("$trades", deployment.Trades);
		command.Parameters.AddWithValue("$profit", Format(deployment.RealizedProfit));
		command.Parameters.AddWithValue("$position", Format(deployment.Position));

		command.Parameters.AddWithValue(
			"$observedAt", deployment.LastObservedAt is { } observed ? Format(observed) : DBNull.Value);

		command.Parameters.AddWithValue("$note", (object)deployment.Note ?? DBNull.Value);
		command.Parameters.AddWithValue("$mode", deployment.Mode.ToString());
		command.Parameters.AddWithValue("$processId", deployment.ProcessId);
		command.Parameters.AddWithValue("$sessionDays", deployment.SessionDays);
	}

	/// <summary>
	/// Reads one deployment.
	/// </summary>
	/// <param name="reader">The row.</param>
	/// <returns>The deployment.</returns>
	private static Deployment ReadDeployment(DbDataReader reader)
		=> new(
			DeploymentId.Parse(reader.GetString(0)),
			CandidateId.Parse(reader.GetString(1)),
			reader.GetString(2),
			ParseNumber(reader.GetString(3)),
			Enum.Parse<TradingModes>(reader.GetString(13)),
			reader.GetInt32(14),
			Enum.Parse<DeploymentStatuses>(reader.GetString(4)),
			ParseMoment(reader.GetString(5)),
			reader.IsDBNull(6) ? null : ParseMoment(reader.GetString(6)),
			reader.GetInt32(7),
			reader.GetInt32(8),
			reader.GetInt32(15),
			ParseNumber(reader.GetString(9)),
			ParseNumber(reader.GetString(10)),
			reader.IsDBNull(11) ? null : ParseMoment(reader.GetString(11)),
			reader.IsDBNull(12) ? null : reader.GetString(12));

	private static RunResult ReadRun(DbDataReader reader)
		=> new(
			RunId.Parse(reader.GetString(0)),
			CandidateId.Parse(reader.GetString(1)),
			DatasetId.Parse(reader.GetString(2)),
			Enum.Parse<DataSlices>(reader.GetString(3)),
			reader.GetInt32(4),
			reader.GetString(5),
			reader.GetString(6),
			reader.GetString(7),
			reader.IsDBNull(17) ? [] : JsonSerializer.Deserialize<Dictionary<string, decimal>>(reader.GetString(17), _json),
			Enum.Parse<RunStatuses>(reader.GetString(8)),
			reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<RunMetrics>(reader.GetString(9), _json),
			ArtifactId.Parse(reader.GetString(10)),
			ArtifactId.Parse(reader.GetString(11)),
			reader.GetInt32(12),
			ParseMoment(reader.GetString(13)),
			ParseMoment(reader.GetString(14)),
			reader.IsDBNull(15) ? null : reader.GetString(15),
			reader.IsDBNull(16) ? null : reader.GetString(16));

	private static void CreateSchema(SqliteConnection connection)
	{
		using var command = connection.CreateCommand();

		command.CommandText =
			$"""
			CREATE TABLE IF NOT EXISTS SchemaInfo (
				Version INTEGER NOT NULL
			);

			INSERT INTO SchemaInfo (Version)
			SELECT {SchemaVersion} WHERE NOT EXISTS (SELECT 1 FROM SchemaInfo);

			CREATE TABLE IF NOT EXISTS Projects (
				Id                TEXT    NOT NULL PRIMARY KEY,
				Name              TEXT    NOT NULL,
				Status            TEXT    NOT NULL,
				CreatedAt         TEXT    NOT NULL,
				UpdatedAt         TEXT    NOT NULL,
				Dataset           TEXT    NULL,
				MaxBacktests      INTEGER NOT NULL,
				MaxCandidates     INTEGER NOT NULL,
				MaxWallClockTicks INTEGER NOT NULL,
				ClaimedBacktests  INTEGER NOT NULL,
				ClaimedCandidates INTEGER NOT NULL,
				ClaimedWallClockTicks INTEGER NOT NULL DEFAULT 0
			);

			CREATE TABLE IF NOT EXISTS AuditEvents (
				Sequence    INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				Type        TEXT    NOT NULL,
				Actor       TEXT    NOT NULL,
				OccurredAt  TEXT    NOT NULL,
				PayloadHash TEXT    NOT NULL,
				Detail      TEXT    NOT NULL
			);

			CREATE TABLE IF NOT EXISTS Candidates (
				Sequence          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				Id                TEXT    NOT NULL UNIQUE,
				Spec              TEXT    NOT NULL,
				Status            TEXT    NOT NULL,
				ClassName         TEXT    NOT NULL,
				SourceHash        TEXT    NOT NULL,
				AssemblyHash      TEXT    NOT NULL,
				Source            TEXT    NOT NULL,
				Assembly          TEXT    NOT NULL,
				TranslatorVersion TEXT    NOT NULL,
				CreatedAt         TEXT    NOT NULL,
				UpdatedAt         TEXT    NOT NULL
			);

			CREATE INDEX IF NOT EXISTS CandidatesBySource ON Candidates (SourceHash);

			CREATE TABLE IF NOT EXISTS Runs (
				Sequence      INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				Id            TEXT    NOT NULL UNIQUE,
				Candidate     TEXT    NOT NULL,
				Dataset       TEXT    NOT NULL,
				Slice         TEXT    NOT NULL,
				Window        INTEGER NOT NULL,
				Symbol        TEXT    NOT NULL,
				Scenario      TEXT    NOT NULL,
				Fingerprint   TEXT    NOT NULL,
				Status        TEXT    NOT NULL,
				Metrics       TEXT    NULL,
				Trades        TEXT    NOT NULL,
				Equity        TEXT    NOT NULL,
				BarsProcessed INTEGER NOT NULL,
				StartedAt     TEXT    NOT NULL,
				FinishedAt    TEXT    NOT NULL,
				Error         TEXT    NULL,
				Diagnosis     TEXT    NULL,
				Parameters    TEXT    NULL
			);

			CREATE INDEX IF NOT EXISTS RunsByFingerprint ON Runs (Fingerprint);
			CREATE INDEX IF NOT EXISTS RunsByCandidate ON Runs (Candidate);

			CREATE TABLE IF NOT EXISTS Measurements (
				Sequence   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				Candidate  TEXT    NOT NULL,
				Content    TEXT    NOT NULL,
				Runs       TEXT    NOT NULL,
				MeasuredAt TEXT    NOT NULL
			);

			CREATE INDEX IF NOT EXISTS MeasurementsByCandidate ON Measurements (Candidate);

			CREATE TABLE IF NOT EXISTS Deployments (
				Sequence       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				Id             TEXT    NOT NULL UNIQUE,
				Candidate      TEXT    NOT NULL,
				Symbol         TEXT    NOT NULL,
				Volume         TEXT    NOT NULL,
				Status         TEXT    NOT NULL,
				StartedAt      TEXT    NOT NULL,
				StoppedAt      TEXT    NULL,
				OrdersPlaced   INTEGER NOT NULL,
				Trades         INTEGER NOT NULL,
				RealizedProfit TEXT    NOT NULL,
				Position       TEXT    NOT NULL,
				LastObservedAt TEXT    NULL,
				Note           TEXT    NULL,
				Mode           TEXT    NOT NULL,
				ProcessId      INTEGER NOT NULL,
				SessionDays    INTEGER NOT NULL
			);

			CREATE INDEX IF NOT EXISTS DeploymentsByCandidate ON Deployments (Candidate);
			""";

		command.ExecuteNonQuery();
	}

	private async ValueTask<ResearchProject> ReadProjectAsync(ProjectId id, CancellationToken cancellationToken)
	{
		var database = Connect(id);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText =
				"""
				SELECT Name, Status, CreatedAt, UpdatedAt, Dataset,
				       MaxBacktests, MaxCandidates, MaxWallClockTicks,
				       ClaimedBacktests, ClaimedCandidates, ClaimedWallClockTicks
				FROM Projects WHERE Id = $id;
				""";

			command.Parameters.AddWithValue("$id", id.Value);

			await using var reader = await command.ExecuteReaderAsync(token);

			if (!await reader.ReadAsync(token))
				return null;

			return new ResearchProject
			{
				Id = id,
				Name = reader.GetString(0),
				Status = Enum.Parse<ProjectStatuses>(reader.GetString(1)),
				CreatedAt = ParseMoment(reader.GetString(2)),
				UpdatedAt = ParseMoment(reader.GetString(3)),
				Dataset = reader.IsDBNull(4) ? default : DatasetId.Parse(reader.GetString(4)),
				Budget = new(
					reader.GetInt32(5),
					reader.GetInt32(6),
					TimeSpan.FromTicks(reader.GetInt64(7)),
					reader.GetInt32(8),
					reader.GetInt32(9),
					TimeSpan.FromTicks(reader.GetInt64(10))),
			};
		}, cancellationToken);
	}

	private async ValueTask<Candidate> ReadCandidateAsync(
		ProjectId project,
		string where,
		string value,
		CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			// Oldest first, so a source hash that appears twice answers with the candidate that claimed it
			// first. The later one is the copy, and pointing at it would move every result already filed
			// under the original.
			command.CommandText = $"SELECT {CandidateColumns} FROM Candidates WHERE {where} ORDER BY Sequence LIMIT 1;";

			command.Parameters.AddWithValue("$value", value);

			await using var reader = await command.ExecuteReaderAsync(token);

			return await reader.ReadAsync(token) ? ReadCandidate(reader) : null;
		}, cancellationToken);
	}

	private async ValueTask<RunResult> ReadRunAsync(
		ProjectId project,
		string where,
		string value,
		CancellationToken cancellationToken)
	{
		if (!Exists(project))
			throw new ProjectNotFoundException(project);

		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = $"SELECT {RunColumns} FROM Runs WHERE {where} ORDER BY Sequence LIMIT 1;";

			command.Parameters.AddWithValue("$value", value);

			await using var reader = await command.ExecuteReaderAsync(token);

			return await reader.ReadAsync(token) ? ReadRun(reader) : null;
		}, cancellationToken);
	}

	// Explicit, because listing candidates takes the same arguments and differs only in what comes back.
	async ValueTask<IReadOnlyList<Deployment>> IDeploymentStore.ListAsync(ProjectId project, CancellationToken cancellationToken)
	{
		var database = Connect(project);

		return await database.RunAsync(async (connection, token) =>
		{
			await using var command = connection.CreateCommand();

			command.CommandText = $"SELECT {DeploymentColumns} FROM Deployments ORDER BY Sequence DESC;";

			await using var reader = await command.ExecuteReaderAsync(token);

			var deployments = new List<Deployment>();

			while (await reader.ReadAsync(token))
				deployments.Add(ReadDeployment(reader));

			return (IReadOnlyList<Deployment>)deployments;
		}, cancellationToken);
	}

	private string FolderOf(ProjectId id)
		=> Path.Combine(_root, id.Value);

	private bool Exists(ProjectId id)
		=> File.Exists(Path.Combine(FolderOf(id), DatabaseFileName));

	private ProjectDatabase Connect(ProjectId id)
	{
		using (_sync.EnterScope())
		{
			if (_databases.TryGetValue(id.Value, out var existing))
				return existing;

			Directory.CreateDirectory(FolderOf(id));

			var connection = SqliteConnections.Open(Path.Combine(FolderOf(id), DatabaseFileName));

			CreateSchema(connection);

			var database = new ProjectDatabase(connection);

			_databases.Add(id.Value, database);

			return database;
		}
	}

	/// <summary>
	/// One project's database and the gate that serializes access to it.
	/// </summary>
	/// <remarks>
	/// A SQLite connection cannot serve two commands at once, and a <see cref="Lock"/> cannot be held
	/// across an await, so the gate is a semaphore rather than a monitor.
	/// </remarks>
	private sealed class ProjectDatabase(SqliteConnection connection) : IDisposable
	{
		private readonly SemaphoreSlim _gate = new(1, 1);

		public async ValueTask<T> RunAsync<T>(
			Func<SqliteConnection, CancellationToken, Task<T>> operation,
			CancellationToken cancellationToken)
		{
			await _gate.WaitAsync(cancellationToken);

			try
			{
				return await operation(connection, cancellationToken);
			}
			finally
			{
				_gate.Release();
			}
		}

		public void Dispose()
		{
			connection.Dispose();
			_gate.Dispose();
		}
	}
}
