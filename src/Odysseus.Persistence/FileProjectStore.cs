namespace StockSharp.Odysseus.Persistence;

using System.Globalization;

/// <summary>Project metadata and research records kept as readable JSON files.</summary>
/// <remarks>
/// One store owns a workspace. Calls within that process are serialized; separate processes must use
/// separate workspaces. Each record is replaced through a temporary file, and file names preserve the
/// order records were added without a separate index.
/// </remarks>
public sealed class FileProjectStore : IProjectStore, IAuditLog, ICandidateStore, IRunStore, IEvaluationStore,
	IDeploymentStore, IDisposable
{
	private readonly string _root;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>Creates a store over the project folders.</summary>
	/// <param name="root">Workspace directory, created if needed.</param>
	public FileProjectStore(string root)
	{
		_root = JsonFiles.PrepareRoot(root);
	}

	/// <inheritdoc />
	public ValueTask CreateAsync(ResearchProject project, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(project);

		return RunAsync(async () =>
		{
			await JsonFiles.WriteAsync(ProjectPath(project.Id), project, cancellationToken, overwrite: false);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask UpdateAsync(ResearchProject project, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(project);

		return RunAsync(async () =>
		{
			var current = await ReadProjectAsync(project.Id, cancellationToken);

			// A caller's snapshot must not undo allowance claimed by another request since it was read.
			await JsonFiles.WriteAsync(ProjectPath(project.Id), current with
			{
				Name = project.Name,
				Status = project.Status,
				UpdatedAt = project.UpdatedAt,
				Dataset = project.Dataset,
			}, cancellationToken);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<ResearchProject> OpenAsync(ProjectId id, CancellationToken cancellationToken)
		=> RunAsync(async () => await ReadProjectAsync(id, cancellationToken), cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<ResearchProject>> ListAsync(CancellationToken cancellationToken)
		=> RunAsync<IReadOnlyList<ResearchProject>>(async () =>
		{
			var projects = new List<ResearchProject>();

			foreach (var folder in Directory.EnumerateDirectories(_root))
			{
				if (!ProjectId.TryParse(Path.GetFileName(folder), out var id) || !File.Exists(ProjectPath(id)))
					continue;

				projects.Add(await ReadProjectAsync(id, cancellationToken));
			}

			return [.. projects.OrderByDescending(p => p.UpdatedAt)];
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask<bool> TryClaimAsync(ProjectId project, int backtests, int candidates, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(backtests);
		ArgumentOutOfRangeException.ThrowIfNegative(candidates);

		return RunAsync(async () =>
		{
			var current = await ReadProjectAsync(project, cancellationToken);
			var budget = current.Budget;

			if ((long)budget.ClaimedBacktests + backtests > budget.MaxBacktests ||
				(long)budget.ClaimedCandidates + candidates > budget.MaxCandidates ||
				budget.ClaimedWallClock >= budget.MaxWallClock)
				return false;

			await JsonFiles.WriteAsync(ProjectPath(project), current with
			{
				Budget = budget with
				{
					ClaimedBacktests = budget.ClaimedBacktests + backtests,
					ClaimedCandidates = budget.ClaimedCandidates + candidates,
				},
			}, cancellationToken);

			return true;
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask ReleaseAsync(ProjectId project, int backtests, int candidates, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(backtests);
		ArgumentOutOfRangeException.ThrowIfNegative(candidates);

		return RunAsync(async () =>
		{
			var current = await ReadProjectAsync(project, cancellationToken);
			await JsonFiles.WriteAsync(ProjectPath(project), current with
			{
				Budget = current.Budget with
				{
					ClaimedBacktests = Math.Max(0, current.Budget.ClaimedBacktests - backtests),
					ClaimedCandidates = Math.Max(0, current.Budget.ClaimedCandidates - candidates),
				},
			}, cancellationToken);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask ChargeTimeAsync(ProjectId project, TimeSpan elapsed, CancellationToken cancellationToken)
		=> elapsed <= TimeSpan.Zero ? ValueTask.CompletedTask : RunAsync(async () =>
		{
			var current = await ReadProjectAsync(project, cancellationToken);
			await JsonFiles.WriteAsync(ProjectPath(project), current with
			{
				Budget = current.Budget with { ClaimedWallClock = current.Budget.ClaimedWallClock + elapsed },
			}, cancellationToken);
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask<AuditEvent> AppendAsync(ProjectId project, AuditEventTypes type, Actors actor,
		string detail, string payloadHash, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(detail);
		ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);

		return RunAsync(async () =>
		{
			EnsureProject(project);
			var folder = RecordsFolder(project, "audit");
			var sequence = NextSequence(folder);
			var entry = new AuditEvent
			{
				Sequence = sequence,
				Type = type,
				Actor = actor,
				OccurredAt = DateTime.UtcNow,
				PayloadHash = payloadHash,
				Detail = detail,
			};

			await JsonFiles.WriteAsync(Path.Combine(folder, SequenceName(sequence) + ".json"), entry,
				cancellationToken, overwrite: false);
			return entry;
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<AuditEvent>> ReadAsync(ProjectId project, CancellationToken cancellationToken)
		=> RunAsync(async () => await ReadRecordsAsync<AuditEvent>(project, "audit", cancellationToken), cancellationToken);

	/// <inheritdoc />
	public ValueTask AddAsync(ProjectId project, Candidate candidate, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		return RunAsync(async () => await AddRecordAsync(project, "candidates", candidate.Id.Value, candidate, cancellationToken), cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask UpdateAsync(ProjectId project, Candidate candidate, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		return RunAsync(async () =>
		{
			var path = FindRecord(project, "candidates", candidate.Id.Value) ?? throw new CandidateNotFoundException(candidate.Id);
			await JsonFiles.WriteAsync(path, candidate, cancellationToken);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<Candidate> GetAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
		=> RunAsync(async () =>
		{
			var path = FindRecord(project, "candidates", candidate.Value) ?? throw new CandidateNotFoundException(candidate);
			return await JsonFiles.ReadAsync<Candidate>(path, cancellationToken);
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask<Candidate> FindBySourceAsync(ProjectId project, string sourceHash, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
		return RunAsync(async () => (await ReadRecordsAsync<Candidate>(project, "candidates", cancellationToken))
			.FirstOrDefault(candidate => candidate.SourceHash == sourceHash), cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<Candidate>> ListAsync(ProjectId project, CancellationToken cancellationToken)
		=> RunAsync(async () => await ReadRecordsAsync<Candidate>(project, "candidates", cancellationToken), cancellationToken);

	/// <inheritdoc />
	public ValueTask AddAsync(ProjectId project, RunResult run, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(run);
		return RunAsync(async () => await AddRecordAsync(project, "runs", run.Id.Value,
			run with { Parameters = run.Parameters ?? new Dictionary<string, decimal>() }, cancellationToken), cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<RunResult> GetAsync(ProjectId project, RunId run, CancellationToken cancellationToken)
		=> RunAsync(async () =>
		{
			var path = FindRecord(project, "runs", run.Value) ?? throw new RunNotFoundException(run);
			return await JsonFiles.ReadAsync<RunResult>(path, cancellationToken);
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask<RunResult> FindAsync(ProjectId project, string fingerprint, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
		return RunAsync(async () => (await ReadRecordsAsync<RunResult>(project, "runs", cancellationToken))
			.FirstOrDefault(run => run.Fingerprint == fingerprint && run.Status == RunStatuses.Completed), cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<RunResult>> ListAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
		=> RunAsync<IReadOnlyList<RunResult>>(async () =>
		{
			var runs = await ReadRecordsAsync<RunResult>(project, "runs", cancellationToken);
			return candidate.IsEmpty ? runs : [.. runs.Where(run => run.Candidate == candidate)];
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask AddAsync(ProjectId project, Deployment deployment, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(deployment);
		return RunAsync(async () => await AddRecordAsync(project, "deployments", deployment.Id.Value, deployment, cancellationToken), cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask UpdateAsync(ProjectId project, Deployment deployment, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(deployment);
		return RunAsync(async () =>
		{
			var path = FindRecord(project, "deployments", deployment.Id.Value) ?? throw new DeploymentNotFoundException(deployment.Id);
			await JsonFiles.WriteAsync(path, deployment, cancellationToken);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<Deployment> GetAsync(ProjectId project, DeploymentId deployment, CancellationToken cancellationToken)
		=> RunAsync(async () =>
		{
			var path = FindRecord(project, "deployments", deployment.Value) ?? throw new DeploymentNotFoundException(deployment);
			return await JsonFiles.ReadAsync<Deployment>(path, cancellationToken);
		}, cancellationToken);

	async ValueTask<IReadOnlyList<Deployment>> IDeploymentStore.ListAsync(ProjectId project, CancellationToken cancellationToken)
		=> await RunAsync<IReadOnlyList<Deployment>>(async () =>
		{
			var records = await ReadRecordsAsync<Deployment>(project, "deployments", cancellationToken);
			return [.. records.Reverse()];
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask AddAsync(ProjectId project, Measurement measurement, IReadOnlyList<RunId> runs, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(measurement);
		ArgumentNullException.ThrowIfNull(runs);
		return RunAsync(async () =>
		{
			EnsureProject(project);
			var folder = RecordsFolder(project, "measurements");
			var path = Path.Combine(folder, SequenceName(NextSequence(folder)) + ".json");
			await JsonFiles.WriteAsync(path, new MeasurementRecord(measurement, runs), cancellationToken, overwrite: false);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<Measurement>> ListMeasurementsAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
		=> RunAsync<IReadOnlyList<Measurement>>(async () => [.. (await ReadRecordsAsync<MeasurementRecord>(project, "measurements", cancellationToken))
			.Where(record => record.Measurement.Candidate == candidate).Select(record => record.Measurement)], cancellationToken);

	/// <inheritdoc />
	public ValueTask<int> CountAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
		=> RunAsync(async () => (await ReadRecordsAsync<MeasurementRecord>(project, "measurements", cancellationToken))
			.Count(record => record.Measurement.Candidate == candidate), cancellationToken);

	/// <inheritdoc />
	public ValueTask<(Measurement Measurement, IReadOnlyList<RunId> Runs)> FindAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
		=> RunAsync<(Measurement, IReadOnlyList<RunId>)>(async () =>
		{
			var record = (await ReadRecordsAsync<MeasurementRecord>(project, "measurements", cancellationToken))
				.LastOrDefault(record => record.Measurement.Candidate == candidate);
			return record is null ? (null, []) : (record.Measurement, record.Runs);
		}, cancellationToken);

	/// <inheritdoc />
	public void Dispose() => _gate.Dispose();

	private string ProjectPath(ProjectId project) => Path.Combine(_root, project.Value, "project.json");

	private string RecordsFolder(ProjectId project, string name) => Path.Combine(_root, project.Value, name);

	private void EnsureProject(ProjectId project)
	{
		if (!File.Exists(ProjectPath(project)))
			throw new ProjectNotFoundException(project);
	}

	private async ValueTask<ResearchProject> ReadProjectAsync(ProjectId project, CancellationToken cancellationToken)
		=> await JsonFiles.ReadAsync<ResearchProject>(ProjectPath(project), cancellationToken) ?? throw new ProjectNotFoundException(project);

	private string FindRecord(ProjectId project, string name, string id)
	{
		EnsureProject(project);
		var folder = RecordsFolder(project, name);
		return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, $"*-{id}.json").SingleOrDefault() : null;
	}

	private async ValueTask AddRecordAsync<T>(ProjectId project, string name, string id, T value, CancellationToken cancellationToken)
	{
		if (FindRecord(project, name, id) is not null)
			throw new InvalidOperationException($"The record '{id}' already exists.");

		var folder = RecordsFolder(project, name);
		var path = Path.Combine(folder, $"{SequenceName(NextSequence(folder))}-{id}.json");
		await JsonFiles.WriteAsync(path, value, cancellationToken, overwrite: false);
	}

	private async ValueTask<IReadOnlyList<T>> ReadRecordsAsync<T>(ProjectId project, string name, CancellationToken cancellationToken)
		where T : class
	{
		EnsureProject(project);
		var records = new List<T>();

		foreach (var path in Files(RecordsFolder(project, name)))
			records.Add(await JsonFiles.ReadAsync<T>(path, cancellationToken));

		return records;
	}

	private static IEnumerable<string> Files(string folder)
		=> Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal) : [];

	private static long NextSequence(string folder)
	{
		var last = Files(folder).LastOrDefault();
		return last is null ? 1 : checked(long.Parse(Path.GetFileNameWithoutExtension(last).Split('-')[0], CultureInfo.InvariantCulture) + 1);
	}

	private static string SequenceName(long sequence) => sequence.ToString("D20", CultureInfo.InvariantCulture);

	private async ValueTask RunAsync(Func<Task> operation, CancellationToken cancellationToken)
	{
		await RunAsync(async () =>
		{
			await operation();
			return true;
		}, cancellationToken);
	}

	private async ValueTask<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);

		try
		{
			return await operation();
		}
		finally
		{
			_gate.Release();
		}
	}

	private sealed record MeasurementRecord(Measurement Measurement, IReadOnlyList<RunId> Runs);
}
