namespace StockSharp.Odysseus.Application.Tests;

using System.Security.Cryptography;
using System.Text;

using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Spec;

/// <summary>
/// Building a specification into something that can be run.
/// </summary>
/// <remarks>
/// The builder itself is stood in for here. What is under test is the accounting around it: what a
/// build costs the project, what happens when the same strategy arrives twice, and what a repeated
/// call over a dropped connection returns. Whether the generated C# compiles is settled elsewhere,
/// against a real compiler.
/// </remarks>
[TestClass]
public class CandidateServiceTests : OdysseusTestBase
{
	private string _root;
	private FileProjectStore _store;
	private FileOperationLog _operations;
	private FileSpecStore _specs;
	private FileArtifactStore _artifacts;
	private RecordingBuilder _builder;
	private CandidateService _service;
	private ProjectId _project;
	private SpecId _spec;

	/// <summary>Builds a service over temporary storage and a project with one specification.</summary>
	[TestInitialize]
	public async Task CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new FileProjectStore(_root);
		_operations = new FileOperationLog(_root);
		_specs = new FileSpecStore(_root);
		_artifacts = new FileArtifactStore(_root);
		_builder = new RecordingBuilder();

		var clock = new BuildClock(new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));

		_service = new CandidateService(_store, _specs, _store, _artifacts, _builder, _store, _operations, clock);

		var project = ResearchProject.Create(
			"breakout study",
			new ResearchBudget(maxBacktests: 60, maxCandidates: 2, maxWallClock: TimeSpan.FromMinutes(45)).ToState(),
			clock.UtcNow);

		await _store.CreateAsync(project, CancellationToken);

		_project = project.Id;

		_spec = (await _specs.AddAsync(_project, SpecJson.Write(Breakout()), Actors.Agent, clock.UtcNow, CancellationToken)).Id;
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteService()
	{
		_store?.Dispose();
		_operations?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>A built candidate carries the whole chain back to the words it came from.</summary>
	[TestMethod]
	public async Task ABuiltCandidateRecordsWhatItCameFrom()
	{
		var candidate = await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		AreEqual(_spec, candidate.Spec);
		AreEqual(CandidateStatuses.Compiled, candidate.Status);
		AreEqual(_builder.Built.ClassName, candidate.ClassName);
		AreEqual(_builder.Built.SourceHash, candidate.SourceHash);
		AreEqual(_builder.Built.AssemblyHash, candidate.AssemblyHash);
		AreEqual(_builder.Built.TranslatorVersion, candidate.TranslatorVersion);
	}

	/// <summary>The generated source is kept, so what ran can be read rather than described.</summary>
	[TestMethod]
	public async Task TheGeneratedSourceIsKeptAndReadable()
	{
		var candidate = await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		AreEqual(_builder.Built.Source, await _service.ReadSourceAsync(_project, candidate.Id, CancellationToken));
	}

	/// <summary>A build costs the project one of its candidates.</summary>
	[TestMethod]
	public async Task ABuildSpendsPartOfTheAllowance()
	{
		await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		var project = await _store.OpenAsync(_project, CancellationToken);

		AreEqual(1, project.Budget.ClaimedCandidates);
	}

	/// <summary>
	/// A specification reworded without changing what it does is the same strategy, and costs nothing.
	/// </summary>
	[TestMethod]
	public async Task RebuildingTheSameStrategyCostsNothing()
	{
		var first = await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		var reworded = await _specs.AddAsync(
			_project,
			SpecJson.Write(Breakout() with
			{
				Name = "Participation confirmed breakout",
				Thesis = "The same rules, described differently.",
			}),
			Actors.Agent,
			new DateTime(2026, 8, 26, 13, 0, 0, DateTimeKind.Utc),
			CancellationToken);

		var second = await _service.BuildAsync(_project, reworded.Id, "op-2", Actors.Agent, CancellationToken);

		AreEqual(first.Id, second.Id, "the same source produced a second candidate.");
		AreEqual(1, (await _store.OpenAsync(_project, CancellationToken)).Budget.ClaimedCandidates);
	}

	/// <summary>A project stops building once it has spent what it was granted.</summary>
	[TestMethod]
	public async Task TheAllowanceIsAHardCeiling()
	{
		await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		_builder.Vary("Second");
		await _service.BuildAsync(_project, _spec, "op-2", Actors.Agent, CancellationToken);

		_builder.Vary("Third");

		var error = await ThrowsAsync<ResearchBudgetExhaustedException>(
			() => _service.BuildAsync(_project, _spec, "op-3", Actors.Agent, CancellationToken).AsTask());

		IsTrue(error.Message.Contains("2 candidates", StringComparison.Ordinal),
			"the refusal does not say what ran out.");
	}

	/// <summary>A repeated call over a dropped connection returns the first result rather than building again.</summary>
	[TestMethod]
	public async Task ARepeatedCallReturnsTheFirstCandidate()
	{
		var first = await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		_builder.Vary("WouldBeDifferent");

		var again = await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		AreEqual(first.Id, again.Id);
		AreEqual(first.SourceHash, again.SourceHash, "the repeated call built a second, different candidate.");
		AreEqual(1, (await _store.OpenAsync(_project, CancellationToken)).Budget.ClaimedCandidates);
	}

	/// <summary>A specification that will not build costs nothing and leaves no candidate behind.</summary>
	[TestMethod]
	public async Task AFailedBuildLeavesTheProjectUntouched()
	{
		_builder.Refuse(new BuildProblem("ODSTR012", "reads the machine clock", 42));

		var error = await ThrowsAsync<StrategyBuildException>(
			() => _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken).AsTask());

		AreEqual(42, error.Problems.Single().Line);
		AreEqual(0, (await _store.OpenAsync(_project, CancellationToken)).Budget.ClaimedCandidates);
		AreEqual(0, (await _service.ListAsync(_project, CancellationToken)).Count);
	}

	/// <summary>The build is written into the permanent record, not only into the candidate.</summary>
	[TestMethod]
	public async Task ABuildIsAudited()
	{
		await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		var events = await _store.ReadAsync(_project, CancellationToken);

		IsTrue(events.Any(e => e.Type == AuditEventTypes.CandidateBuilt),
			"a candidate was built without the project recording it.");
	}

	/// <summary>Candidates are listed in the order they were built.</summary>
	[TestMethod]
	public async Task CandidatesAreListedOldestFirst()
	{
		var first = await _service.BuildAsync(_project, _spec, "op-1", Actors.Agent, CancellationToken);

		_builder.Vary("Second");

		var second = await _service.BuildAsync(_project, _spec, "op-2", Actors.Agent, CancellationToken);

		var listed = await _service.ListAsync(_project, CancellationToken);

		AreEqual(2, listed.Count);
		AreEqual(first.Id, listed[0].Id);
		AreEqual(second.Id, listed[1].Id);
	}

	/// <summary>Asking for a candidate that does not exist says so rather than returning nothing.</summary>
	[TestMethod]
	public async Task AnUnknownCandidateIsRefused()
		=> await ThrowsAsync<CandidateNotFoundException>(
			() => _service.GetAsync(_project, CandidateId.New(), CancellationToken).AsTask());

	private static StrategySpec Breakout()
		=> new()
		{
			Name = "Volume confirmed breakout",
			Thesis = "A close above a recent high carries on when participation confirms it.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 61,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("highest", new ParameterRef("BreakoutPeriod"), CandleFields.High, Offset: 1))),
			],
			Exits =
			[
				new("x1", ExitKinds.AtrStop, TradeDirections.Long, Length: new Constant(14), Multiplier: new Constant(2)),
				new("x2", ExitKinds.SessionEnd, TradeDirections.Long),
			],
			Parameters = [new("BreakoutPeriod", ParameterTypes.Integer, Default: 20, Minimum: 10, Maximum: 60, Step: 5)],
			Risk = new(0.10m, 0.02m),
		};

	/// <summary>
	/// A builder that reports what it was asked to build, so the accounting around it can be checked
	/// without a compiler in the way.
	/// </summary>
	/// <remarks>
	/// The identity it reports is derived from the specification, as the real translator's is: over what
	/// the strategy does, with the name and the thesis left out. A constant would make the deduplication
	/// test pass over a builder that had never looked at what it was handed.
	/// </remarks>
	private sealed class RecordingBuilder : IStrategyBuilder
	{
		private string _variant = "First";
		private BuildProblem _refusal;

		public BuiltStrategy Built { get; private set; }

		public void Vary(string variant)
			=> _variant = variant;

		public void Refuse(BuildProblem problem)
			=> _refusal = problem;

		public BuiltStrategy Build(StrategySpec spec)
		{
			if (_refusal is not null)
				throw new StrategyBuildException([_refusal]);

			var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
				_variant + SpecJson.Write(spec with { Name = string.Empty, Thesis = string.Empty }))));

			Built = new(
				$"Generated{_variant}",
				$"// {_variant} source of {spec.Name}",
				identity,
				[1, 2, 3, .. _variant.Select(c => (byte)c)],
				$"assembly-{_variant}",
				"9.9.9");

			return Built;
		}
	}

	private sealed class BuildClock(DateTime now) : IClock
	{
		public DateTime UtcNow => now;
	}
}
