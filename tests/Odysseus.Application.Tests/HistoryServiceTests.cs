namespace Odysseus.Application.Tests;

using System.Globalization;
using System.Threading;

using Waiting = System.Threading.Timeout;

using Odysseus.Persistence;
using Odysseus.Platform;

/// <summary>
/// Downloading history from a broker, making it a project's data, and measuring what is in it.
/// </summary>
/// <remarks>
/// A broker that answers slowly and a broker that never answers look the same from here. The second one
/// is the dangerous one: the agent that asked is left holding a call that will not return, with no error
/// to read and nothing to decide from, and the project it was working on cannot go on without it.
///
/// The other dangerous shape is a download that half worked: a dataset over part of what was asked for
/// is research over something nobody asked about.
/// </remarks>
[TestClass]
public class HistoryServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);
	private static readonly DateTime _from = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime _to = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileDatasetStore _datasets;
	private ProjectService _projects;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));

		var budget = new ResearchBudget(60, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, new SystemClock(), ServerModes.Local, budget);
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

	/// <summary>A broker that goes quiet ends the import with an error rather than never ending it.</summary>
	[TestMethod]
	public async Task ADownloadThatNeverAnswersGivesUp()
	{
		var project = await _projects.CreateProjectAsync("silent broker", "new-1", Actors.Agent, CancellationToken);

		var service = new HistoryService(
			_store,
			_datasets,
			new SilentSource(),
			new StockSharpMarketProfiler(),
			_store,
			_operations,
			new SystemClock())
		{
			Patience = TimeSpan.FromSeconds(2),
		};

		var refusal = await ThrowsAsync<TimeoutException>(() => service.ImportAsync(
			project.Id,
			["NVDA"],
			TimeSpan.FromMinutes(5),
			new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
			new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			"import-1",
			Actors.Agent,
			CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("NVDA", StringComparison.Ordinal),
			$"the refusal does not say which symbol went quiet: {refusal.Message}");
	}

	/// <summary>
	/// A finished download becomes the project's data, and says where it came from.
	/// </summary>
	[TestMethod]
	public async Task ImportingMakesWhatWasDownloadedTheProjectsData()
	{
		var project = await ProjectAsync("import");
		var source = new Fixture(Bars());
		var service = Service(source);

		var manifest = await service.ImportAsync(
			project, ["NVDA"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		AreEqual("fixture", manifest.Source,
			"the dataset does not record which connector its bars came through.");

		IsFalse(manifest.IsSynthetic, "history downloaded from a broker was recorded as generated data.");
		AreEqual(1, manifest.Symbols.Count, "the dataset covers something other than the one symbol asked for.");

		AreEqual(manifest.Id, (await _store.OpenAsync(project, CancellationToken)).Dataset,
			"the project was not given the data that was imported for it.");

		var imported = (await _projects.ReadAuditAsync(project, CancellationToken))
			.Single(e => e.Type == AuditEventTypes.DatasetImported);

		IsTrue(imported.Detail.Contains("NVDA", StringComparison.Ordinal),
			$"the record does not say what was imported: {imported.Detail}");

		IsTrue(imported.Detail.Contains(manifest.Split.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal),
			$"the record does not say which range was imported: {imported.Detail}");
	}

	/// <summary>
	/// Asking twice with one key imports one dataset and does not go back to the broker.
	/// </summary>
	[TestMethod]
	public async Task RepeatingTheRequestDoesNotDownloadTheHistoryAgain()
	{
		var project = await ProjectAsync("retry");
		var source = new Fixture(Bars());
		var service = Service(source);

		var first = await service.ImportAsync(
			project, ["NVDA"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		var again = await service.ImportAsync(
			project, ["NVDA"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		AreEqual(first.Id, again.Id, "a retry of one request produced a second dataset.");
		AreEqual(1, source.Downloads, $"a retry of one request went back to the broker {source.Downloads} times.");
	}

	/// <summary>
	/// A symbol the broker has nothing for stops the whole import, and the project keeps its old data.
	/// </summary>
	/// <remarks>
	/// A dataset over one of the two symbols asked for would be a different question from the one asked.
	/// </remarks>
	[TestMethod]
	public async Task ASymbolTheBrokerHasNothingForImportsNothingAtAll()
	{
		var project = await ProjectAsync("empty symbol");
		var source = new Fixture(Bars());
		var service = Service(source);

		source.Unknown.Add("GOOG");

		var refusal = await ThrowsAsync<ArgumentException>(() => service.ImportAsync(
			project, ["NVDA", "GOOG"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("GOOG", StringComparison.Ordinal),
			$"the refusal does not say which symbol came back empty: {refusal.Message}");

		IsTrue((await _store.OpenAsync(project, CancellationToken)).Dataset.IsEmpty,
			"part of what was asked for was made the project's data.");

		IsFalse((await _projects.ReadAuditAsync(project, CancellationToken)).Any(e => e.Type == AuditEventTypes.DatasetImported),
			"an import that kept nothing was written down as though it had.");

		// The refusal did not spend the request: the caller is being told to correct the symbol and ask again.
		source.Unknown.Clear();

		var manifest = await service.ImportAsync(
			project, ["NVDA", "GOOG"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		AreEqual(2, manifest.Symbols.Count,
			"the refused request took the key with it, so asking again after correcting the symbol was answered with the refusal.");
	}

	/// <summary>
	/// A project with no data has nothing to measure, and the refusal says how to give it some.
	/// </summary>
	[TestMethod]
	public async Task AProjectWithNoDataHasNothingToMeasure()
	{
		var project = await ProjectAsync("nothing to measure");

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => Service(new Fixture(Bars())).AnalyseAsync(project, null, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("import_history", StringComparison.Ordinal) &&
				refusal.Message.Contains("import_demo_dataset", StringComparison.Ordinal),
			$"the refusal does not say how to get data into the project: {refusal.Message}");
	}

	/// <summary>
	/// What is measured is the development slice, and it stops where development stops.
	/// </summary>
	/// <remarks>
	/// The profile is what an agent forms its first hypothesis from. Measured over the validation or the
	/// closed slice it would be a hypothesis formed on the data reserved for judging hypotheses, which is
	/// the same leak as reading the answers and is far harder to notice afterwards.
	/// </remarks>
	[TestMethod]
	public async Task OnlyTheDevelopmentSliceIsMeasured()
	{
		var project = await ProjectAsync("profile");
		var bars = Bars();
		var service = Service(new Fixture(bars));

		var manifest = await service.ImportAsync(
			project, ["NVDA"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		var profile = await service.AnalyseAsync(project, "NVDA", CancellationToken);

		var development = bars.Where(b => b.OpenTime < manifest.Split.DevelopmentTo).ToArray();

		AreEqual("NVDA", profile.Coverage.Symbol, "the profile is not of the instrument it was asked about.");

		AreEqual(development.Length, profile.Coverage.Bars,
			$"the profile was measured over {profile.Coverage.Bars} bars, and development holds {development.Length}.");

		AreEqual(development[0].OpenTime, profile.Coverage.From, "the profile starts somewhere other than the data does.");

		AreEqual(development[^1].OpenTime, profile.Coverage.To,
			"the profile ends somewhere other than at the last bar of development.");

		IsTrue(profile.Coverage.To < manifest.Split.DevelopmentTo,
			"the profile reaches past the end of development, into data held back for judging what it suggests.");
	}

	/// <summary>
	/// With no instrument named, the first one the dataset lists is measured.
	/// </summary>
	/// <remarks>
	/// "The first one" is the first the dataset lists, not the first the caller asked for. A dataset
	/// orders its symbols so that the same import always divides and reads the same way, which is why
	/// naming NVDA first and being answered about AAPL is the behaviour rather than a mix-up.
	/// </remarks>
	[TestMethod]
	public async Task WithNoInstrumentNamedTheFirstOneTheDatasetListsIsMeasured()
	{
		var project = await ProjectAsync("first symbol");
		var service = Service(new Fixture(Bars()));

		var manifest = await service.ImportAsync(
			project, ["NVDA", "AAPL"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		var profile = await service.AnalyseAsync(project, null, CancellationToken);

		AreEqual(manifest.Symbols[0], profile.Coverage.Symbol,
			"with nothing named, the profile is not of the first instrument the dataset lists.");

		AreEqual("AAPL", profile.Coverage.Symbol,
			"the instruments are listed in the order they were asked for rather than in a settled one, so which " +
			"instrument gets measured by default depends on how the import was written.");
	}

	/// <summary>An instrument the project does not hold is refused, with what it does hold.</summary>
	[TestMethod]
	public async Task AnInstrumentTheProjectDoesNotHoldIsRefused()
	{
		var project = await ProjectAsync("wrong symbol");
		var service = Service(new Fixture(Bars()));

		await service.ImportAsync(
			project, ["NVDA"], TimeSpan.FromMinutes(5), _from, _to, "import-1", Actors.Agent, CancellationToken);

		var refusal = await ThrowsAsync<ArgumentException>(
			() => service.AnalyseAsync(project, "TSLA", CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("TSLA", StringComparison.Ordinal),
			$"the refusal does not say which instrument was asked for: {refusal.Message}");

		IsTrue(refusal.Message.Contains("NVDA", StringComparison.Ordinal),
			$"the refusal does not say what the project does hold: {refusal.Message}");
	}

	/// <summary>Enough bars for a split with something in every slice, over several trading days.</summary>
	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var time = _open;

		for (var i = 0; i < 3_000; i++)
		{
			var close = 100m + Math.Round(5m * (decimal)Math.Sin(i * 2 * Math.PI / 60), 2);
			var open = bars.Count == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 10_000m));

			time = time.AddMinutes(5);

			if (time.TimeOfDay >= TimeSpan.FromHours(21))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}

	private HistoryService Service(IHistorySource source)
		=> new(_store, _datasets, source, new StockSharpMarketProfiler(), _store, _operations, new SystemClock())
		{
			Patience = TimeSpan.FromSeconds(30),
		};

	private async Task<ProjectId> ProjectAsync(string name)
		=> (await _projects.CreateProjectAsync(name, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken)).Id;

	/// <summary>A source that accepts the request and then says nothing at all.</summary>
	private sealed class SilentSource : IHistorySource
	{
		public string SourceName => "silent";

		public async Task<IReadOnlyList<Candle>> GetBarsAsync(
			string symbol,
			TimeSpan timeFrame,
			DateTime from,
			DateTime to,
			CancellationToken cancellationToken)
		{
			await Task.Delay(Waiting.InfiniteTimeSpan, cancellationToken);
			throw new InvalidOperationException("unreachable");
		}
	}

	/// <summary>A source that answers with the same bars, and remembers what it was asked.</summary>
	private sealed class Fixture : IHistorySource
	{
		private readonly IReadOnlyList<Candle> _bars;

		public Fixture(IReadOnlyList<Candle> bars)
		{
			_bars = bars;
		}

		public string SourceName => "fixture";

		/// <summary>Symbols it holds nothing for, which is how a broker answers a ticker that is wrong.</summary>
		public HashSet<string> Unknown { get; } = new(StringComparer.Ordinal);

		/// <summary>How many times it was asked for bars.</summary>
		public int Downloads { get; private set; }

		public Task<IReadOnlyList<Candle>> GetBarsAsync(
			string symbol,
			TimeSpan timeFrame,
			DateTime from,
			DateTime to,
			CancellationToken cancellationToken)
		{
			Downloads++;

			return Task.FromResult<IReadOnlyList<Candle>>(Unknown.Contains(symbol) ? [] : _bars);
		}
	}
}
