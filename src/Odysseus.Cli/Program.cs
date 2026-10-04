namespace Odysseus.Cli;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Cli.Console;
using Odysseus.Domain;
using Odysseus.Spec;

using Con = System.Console;

/// <summary>
/// The command line over the same research the MCP server exposes.
/// </summary>
/// <remarks>
/// One set of services, two ways in. The MCP server answers an agent in JSON; this answers a person in
/// columns. Nothing here computes anything of its own - a number printed by a command is the number the
/// same call would return over the protocol, which is what makes the two worth having side by side.
/// </remarks>
public static class Program
{
	/// <summary>
	/// Runs one command.
	/// </summary>
	/// <param name="args">Command and its arguments.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		Con.OutputEncoding = System.Text.Encoding.UTF8;

		Ui.UseColour(Environment.GetEnvironmentVariable("NO_COLOR") is null);

		// Asking what the commands are opens nothing. A workspace makes the projects directory and the
		// databases under it, and help is the one command that has to answer on a machine where nothing
		// has been started yet.
		if (IsHelp(args))
		{
			Usage();
			return 0;
		}

		using var workspace = Workspace.Open();

		return await Run(args, workspace, ConsoleTerminal.Instance);
	}

	/// <summary>
	/// Runs one command against an open workspace.
	/// </summary>
	/// <param name="args">Command and its arguments.</param>
	/// <param name="workspace">Everything a command needs.</param>
	/// <param name="terminal">Where a person can be asked something, and which says whether there is one.</param>
	/// <returns>Process exit code.</returns>
	/// <remarks>
	/// The whole command surface, with the workspace and the person handed to it rather than reached for.
	/// <see cref="Main"/> is then only what a process does: the encoding, the colour, and opening the
	/// workspace the environment points at.
	/// </remarks>
	public static async Task<int> Run(string[] args, Workspace workspace, IOperatorTerminal terminal)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(terminal);

		if (IsHelp(args))
		{
			Usage();
			return 0;
		}

		try
		{
			return args[0] switch
			{
				"projects" => await Projects(workspace),
				"new" => await New(workspace, Rest(args)),
				"import" => await Import(workspace, Rest(args)),
				"demo" => await Demo(workspace),
				"analyze" => await Analyze(workspace, Rest(args)),
				"propose" => await Propose(workspace, Rest(args)),
				"build" => await Build(workspace, Rest(args)),
				"source" => await Source(workspace, Rest(args)),
				"backtest" => await Backtest(workspace, Rest(args)),
				"explain" => await Explain(workspace, Rest(args)),
				"runs" => await Runs(workspace, Rest(args)),
				"measure" => await Measure(workspace, Rest(args)),
				"closed" => await Closed(workspace),
				"complete" => await Complete(workspace, Rest(args)),
				"completed" => await CompletedList(workspace),
				"runners" => await Runners(workspace),
				"runner" => await Runner(workspace, terminal, Rest(args)),
				"mandate" => Mandate(Rest(args)),
				_ => Unknown(args[0]),
			};
		}
		catch (Exception error)
		{
			Ui.Blank();
			Ui.Refuse(error.Message);
			Ui.Blank();

			return 1;
		}
	}

	private static bool IsHelp(string[] args)
		=> args.Length == 0 || args[0] is "help" or "--help" or "-h";

	private static string[] Rest(string[] args) => [.. args.Skip(1)];

	// An argument this command did not read is refused rather than passed over. A mistyped --close on a
	// stop would otherwise leave a position open under an answer that said it had been closed.
	private static int Extra(string argument, string form)
	{
		Ui.Refuse($"'{argument}' is not an argument of this command. {form}");

		return 1;
	}

	private static int Unknown(string command)
	{
		Ui.Refuse($"There is no command '{command}'.");
		Usage();

		return 1;
	}

	private static void Usage()
	{
		Ui.Title("odysseus", "research from the command line");
		Ui.Blank();

		Ui.Table(
			["command", "what it does"],
			[
				["projects", "list the projects on this machine"],
				["new <name>", "start a project"],
				["import <symbol> [days] [tf]", "download real history and make it the project's data"],
				["demo", "generated bars, to try the machinery without an account"],
				["analyze [symbol]", "measure what the history holds"],
				["propose <file.json>", "record a hypothesis"],
				["build", "turn it into a StockSharp strategy and compile"],
				["source [candidate]", "print the C# that was compiled"],
				["backtest [symbol]", "run it and measure"],
				["explain [run]", "cut the result apart"],
				["runs", "list what has been run"],
				["measure [symbol]", "the six runs, reported side by side"],
				["closed", "spend the one measurement on the closed history"],
				["complete <why>", "call it finished and keep it"],
				["completed", "what this project has been finished with"],
				["runners", "every deployment still trading on this machine"],
				["runner stop <id> [--close]", "end one, saying what to do with its position"],
				["runner kill <id>", "end the process itself, which decides nothing about the position"],
				["mandate template", "print an example live mandate for a person to save and edit"],
			]);

		Ui.Blank();
		Ui.Aside("  A project is remembered between commands, so the symbol and the identifiers are optional.");
		Ui.Aside("  ODYSSEUS_PROJECTS_ROOT says where projects live, ODYSSEUS_MARKET_DATA where the history every");
		Ui.Aside("  project shares is kept; ODYSSEUS_BROKER_KEYS points at the key file and");
		Ui.Aside("  ODYSSEUS_BROKER_CONNECTOR at the connector file that says which broker to load.");
		Ui.Blank();
	}

	private static async Task<int> Projects(Workspace workspace)
	{
		var projects = await workspace.Projects.ListProjectsAsync(CancellationToken.None);

		Ui.Title("projects", workspace.Root);

		if (projects.Count == 0)
		{
			Ui.Blank();
			Ui.Aside("  Nothing here yet. Start one with: odysseus new \"my study\"");
			Ui.Blank();

			return 0;
		}

		Ui.Blank();
		Ui.Table(
			["id", "name", "status", "changed"],
			[.. projects.Select(p => new[]
			{
				p.Id.Value,
				p.Name,
				p.Status.ToString(),
				p.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
			})]);

		Ui.Blank();

		return 0;
	}

	private static async Task<int> New(Workspace workspace, string[] args)
	{
		var name = args.Length > 0 ? string.Join(' ', args) : "study";

		var project = await workspace.Projects.CreateProjectAsync(
			name, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		workspace.Remember(project.Id);

		Ui.Title("project", project.Id.Value);
		Ui.Blank();
		Ui.Figures(
			("name", project.Name),
			("status", project.Status.ToString()),
			("backtests", project.Budget.MaxBacktests.ToString(CultureInfo.InvariantCulture)),
			("candidates", project.Budget.MaxCandidates.ToString(CultureInfo.InvariantCulture)));
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Import(Workspace workspace, string[] args)
	{
		var symbol = args.Length > 0 ? args[0] : throw new ArgumentException("Name a symbol: odysseus import NVDA");
		var days = args.Length > 1 ? Days(args[1]) : 90;
		var frame = args.Length > 2 ? Frame(args[2]) : TimeSpan.FromMinutes(5);

		var project = workspace.Current();
		var to = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc);

		Ui.Title("import", $"{symbol} · {frame:hh\\:mm} · {days} days");
		Ui.Blank();
		// The connector is loaded when something needs it rather than when the process starts, so that
		// every command which only reads what is already imported costs no download and needs no network.
		if (workspace.RemoteStorage is null && !await workspace.UseBrokerAsync(CancellationToken.None))
		{
			throw new InvalidOperationException(
				"No broker. Point ODYSSEUS_BROKER_CONNECTOR at a connector file naming the package to load, " +
				"and ODYSSEUS_BROKER_KEYS at the credentials it takes; or ODYSSEUS_REMOTE_STORAGE at a file " +
				"naming a StockSharp storage server to import from.");
		}

		Ui.Aside(workspace.RemoteStorage is null ? "  asking the broker…" : $"  asking {workspace.RemoteStorage}…");

		var manifest = await workspace.HistoryService.ImportAsync(
			project, [symbol], frame, to.AddDays(-days), to,
			Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		var quality = manifest.Quality[0];

		Ui.Blank();
		Ui.Table(
			["symbol", "bars", "from", "to", "gaps", "duplicates", "malformed"],
			[[
				quality.Symbol,
				quality.Records.ToString("N0", CultureInfo.InvariantCulture),
				quality.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				quality.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				quality.Gaps.ToString(CultureInfo.InvariantCulture),
				quality.Duplicates.ToString(CultureInfo.InvariantCulture),
				quality.Invalid.ToString(CultureInfo.InvariantCulture),
			]],
			1, 4, 5, 6);

		Ui.Blank();
		Ui.Aside($"  dataset {manifest.Id.Value}  ·  from {manifest.Source}  ·  stored in {workspace.MarketData}");
		Ui.Aside($"  development to {manifest.Split.DevelopmentTo:yyyy-MM-dd}, validation to {manifest.Split.ValidationTo:yyyy-MM-dd}, closed to {manifest.Split.To:yyyy-MM-dd}");
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Demo(Workspace workspace)
	{
		var project = workspace.Current();

		var manifest = await workspace.Datasets.ImportDemoAsync(
			project, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		Ui.Title("demo data", "generated, not observed");
		Ui.Blank();

		Ui.Table(
			["symbol", "bars", "from", "to", "gaps"],
			[.. manifest.Quality.Select(q => new[]
			{
				q.Symbol,
				q.Records.ToString("N0", CultureInfo.InvariantCulture),
				q.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				q.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				q.Gaps.ToString(CultureInfo.InvariantCulture),
			})],
			1, 4);

		Ui.Blank();
		Ui.Aside("  ! These bars were generated. A result measured on them says nothing about any market.");
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Analyze(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var profile = await workspace.HistoryService.AnalyseAsync(
			project, args.Length > 0 ? args[0] : null, CancellationToken.None);

		var c = profile.Coverage;
		var m = profile.Movement;
		var p = profile.Persistence;
		var e = profile.Edge;

		Ui.Title("market", $"{c.Symbol} · {c.Bars:N0} bars · {c.Sessions} sessions");
		Ui.Blank();

		Ui.Figures(
			("bar range, median", Ui.Number(m.MedianBarRangePercent) + " %"),
			("day range, median", Ui.Number(m.MedianDailyRangePercent) + " %"),
			("volatility, annual", Ui.Number(m.AnnualisedVolatilityPercent) + " %"),
			("volume, median bar", c.MedianVolume.ToString("N0", CultureInfo.InvariantCulture)));

		Ui.Blank();
		Ui.Say("  does it keep going, or come back?");
		Ui.Blank();

		Ui.Table(
			["measure", "value", "reads as"],
			[
				["variance ratio, 5 bars", Ui.Number(p.VarianceRatio5), Reads(p.VarianceRatio5)],
				["variance ratio, 20 bars", Ui.Number(p.VarianceRatio20), Reads(p.VarianceRatio20)],
				["autocorrelation, 1 bar", Ui.Number(p.Autocorrelation1, 3), "how much one bar says about the next"],
				["directional days", Ui.Number(p.DirectionalDayShare, 1) + " %", "days that went one way and stayed"],
			],
			1);

		Ui.Blank();
		Ui.Say("  what followed the events");
		Ui.Blank();

		Ui.Table(
			["event", "times", "next 5 bars", "still up after"],
			[
				["breakout", e.BreakoutCount.ToString(CultureInfo.InvariantCulture), Ui.Signed(e.BreakoutFollowThroughPercent) + " %", Ui.Number(e.BreakoutPositiveShare, 1) + " %"],
				["breakdown", e.BreakdownCount.ToString(CultureInfo.InvariantCulture), Ui.Signed(e.BreakdownFollowThroughPercent) + " %", ""],
				["stretched past 2σ", e.StretchCount.ToString(CultureInfo.InvariantCulture), Ui.Signed(e.StretchReversionPercent) + " %", ""],
			],
			1, 2, 3);

		Ui.Blank();
		Ui.Say("  where the session sits");
		Ui.Blank();

		Ui.Table(
			["part", "of the range", "of the volume", "average move"],
			[.. profile.Session.Select(s => new[]
			{
				s.Bucket,
				Ui.Number(s.ShareOfRange, 1) + " %",
				Ui.Number(s.ShareOfVolume, 1) + " %",
				Ui.Signed(s.AverageReturnPercent) + " %",
			})],
			1, 2, 3);

		if (profile.Caveats.Count > 0)
		{
			Ui.Blank();

			foreach (var caveat in profile.Caveats)
				Ui.Aside("  ! " + caveat);
		}

		Ui.Blank();
		Ui.Aside("  Numbers only. What they mean about a hypothesis is not this program's to say.");
		Ui.Blank();

		return 0;
	}

	// A run with no losing trade has no ratio to report, which is a fact rather than a blank.
	private static string Factor(decimal? factor)
		=> factor is null ? "no losses" : Ui.Number(factor.Value);

	private static string Reads(decimal ratio)
		=> ratio > 1.05m ? "moves extend" : ratio < 0.95m ? "moves come back" : "no memory at this horizon";

	private static async Task<int> Propose(Workspace workspace, string[] args)
	{
		if (args.Length == 0)
			throw new ArgumentException("Name a file: odysseus propose hypothesis.json");

		var json = await File.ReadAllTextAsync(args[0], CancellationToken.None);
		var project = workspace.Current();

		var check = SpecValidator.Validate(SpecJson.Read(json));

		if (!check.IsValid)
		{
			Ui.Title("hypothesis", "refused");
			Ui.Blank();
			Ui.Table(
				["where", "what is wrong", "what would fix it"],
				[.. check.Problems.Select(p => new[] { p.Path, p.Message, p.Remedy })]);
			Ui.Blank();

			return 1;
		}

		var revision = await workspace.Specs.ProposeAsync(
			project, json, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		workspace.RememberSpec(revision.Id);

		Ui.Title("hypothesis", $"revision {revision.Revision}");
		Ui.Blank();
		Ui.Figures(("id", revision.Id.Value), ("hash", revision.Hash[..16] + "…"));
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Build(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var spec = args.Length > 0 ? SpecId.Parse(args[0]) : workspace.CurrentSpec();

		Ui.Title("build", "translating and compiling");

		var candidate = await workspace.Candidates.BuildAsync(
			project, spec, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		workspace.RememberCandidate(candidate.Id);

		Ui.Blank();
		Ui.Figures(
			("class", candidate.ClassName),
			("status", candidate.Status.ToString()),
			("translator", candidate.TranslatorVersion),
			("source", candidate.SourceHash[..12] + "…"));

		Ui.Blank();
		Ui.Aside("  An ordinary StockSharp strategy: odysseus source shows the C# it compiled.");
		Ui.Blank();

		return 0;
	}

	/// <summary>
	/// Prints the C# a candidate was compiled from.
	/// </summary>
	/// <param name="workspace">Everything a command needs.</param>
	/// <param name="args">The candidate, or none for the one last built.</param>
	/// <returns>Process exit code.</returns>
	/// <remarks>
	/// The same text <c>get_candidate_source</c> returns over the protocol. A generated strategy is
	/// ordinary source that a person is entitled to read before they believe a number measured on it,
	/// and a claim to that effect which no command answered was worth less than nothing.
	/// </remarks>
	private static async Task<int> Source(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var candidate = args.Length > 0 ? CandidateId.Parse(args[0]) : workspace.CurrentCandidate();

		var source = await workspace.Candidates.ReadSourceAsync(project, candidate, CancellationToken.None);

		Ui.Title("source", candidate.Value);
		Ui.Blank();

		Con.WriteLine(source);

		Ui.Blank();

		return 0;
	}

	private static async Task<int> Backtest(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var candidate = workspace.CurrentCandidate();
		var symbol = args.Length > 0 ? args[0] : null;

		Ui.Title("backtest", "development slice, baseline costs");
		Ui.Blank();
		Ui.Aside("  running…");

		var (run, _) = await workspace.Backtests.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, symbol, RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		workspace.RememberRun(run.Id);

		if (run.Status != RunStatuses.Completed)
		{
			Ui.Blank();
			Ui.Refuse(run.Error);
			Ui.Blank();

			return 1;
		}

		var m = run.Metrics;

		Ui.Blank();
		Ui.Figures(
			("bars", run.BarsProcessed.ToString("N0", CultureInfo.InvariantCulture)),
			("trades", m.Trades.Count.ToString(CultureInfo.InvariantCulture)),
			("won", Ui.Number(m.Trades.WinRatePercent, 1) + " %"),
			("held, average", Ui.Number(m.Trades.AverageHoldingMinutes, 0) + " min"));

		Ui.Blank();
		Ui.Table(
			["", "before costs", "after costs"],
			[
				["profit", Ui.Signed(m.Gross.Profit), Ui.Signed(m.Net.Profit)],
				["return", Ui.Signed(m.Gross.ReturnPercent) + " %", Ui.Signed(m.Net.ReturnPercent) + " %"],
				["profit factor", Factor(m.Gross.ProfitFactor), Factor(m.Net.ProfitFactor)],
				["one trade, average", Ui.Signed(m.Gross.AverageTrade), Ui.Signed(m.Net.AverageTrade)],
			],
			1, 2);

		Ui.Blank();
		Ui.Table(
			["charged", "amount", "what it is"],
			[
				["commission", Ui.Signed(-m.Costs.Commission), "fees, which follow the volume"],
				["spread", Ui.Signed(-m.Costs.Slippage), "crossing, which holding longer pays less often"],
			],
			1);

		Ui.Blank();

		var equity = await workspace.Backtests.ReadEquityAsync(project, run.Id, CancellationToken.None);

		Ui.Say("  account through the run");
		Ui.Blank();
		Ui.Chart([.. equity.Select(p => p.Equity)], 96, 9, BacktestService.StartingEquity);

		Ui.Blank();
		Ui.Figures(
			("drawdown, deepest", Ui.Number(m.Risk.MaxDrawdownPercent, 2) + " %"),
			("in the market", Ui.Number(m.Activity.ExposurePercent, 1) + " %"),
			("biggest trade's share", Ui.Number(m.Concentration.LargestTradeProfitSharePercent, 1) + " %"));

		if (run.Diagnosis is { Length: > 0 })
		{
			Ui.Blank();
			Ui.Aside("  " + run.Diagnosis);
		}

		Ui.Blank();
		Ui.Aside($"  {run.Id.Value}   ·   odysseus explain   cuts this apart");
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Measure(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var candidate = workspace.CurrentCandidate();
		var symbol = args.Length > 0 ? args[0] : "";

		Ui.Title("measure", "six runs: development, held out, held out under heavier costs, three windows");
		Ui.Blank();
		Ui.Aside("  running…");

		var result = await workspace.Evaluations.MeasureAsync(
			project, candidate, symbol, null, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		Ui.Blank();
		Report(result.Measurement);

		Ui.Blank();
		Ui.Aside($"  measured {result.TimesMeasured} time(s) on the open data.");
		Ui.Aside("  These are numbers, not a verdict. What they are worth is yours to decide.");
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Closed(Workspace workspace)
	{
		var project = workspace.Current();
		var candidate = workspace.CurrentCandidate();

		Ui.Title("closed", "the one measurement that cannot be taken twice");
		Ui.Blank();
		Ui.Aside("  running…");

		var result = await workspace.Closed.MeasureAsync(
			project, candidate, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken.None);

		var open = result.OnOpenData;
		var closed = result.Measurement;

		Ui.Blank();
		Ui.Table(
			["", "on data it saw", "on data it did not"],
			[
				["profit", Ui.Signed(open.HeldOut.Net.Profit), Ui.Signed(closed.HeldOut.Net.Profit)],
				["return", Ui.Signed(open.HeldOut.Net.ReturnPercent) + " %", Ui.Signed(closed.HeldOut.Net.ReturnPercent) + " %"],
				["deepest fall", Ui.Number(open.HeldOut.Risk.MaxDrawdownPercent, 2) + " %", Ui.Number(closed.HeldOut.Risk.MaxDrawdownPercent, 2) + " %"],
				["trades", open.HeldOut.Trades.Count.ToString(CultureInfo.InvariantCulture), closed.HeldOut.Trades.Count.ToString(CultureInfo.InvariantCulture)],
				["won", Ui.Number(open.HeldOut.Trades.WinRatePercent, 1) + " %", Ui.Number(closed.HeldOut.Trades.WinRatePercent, 1) + " %"],
				["under heavier costs", Ui.Signed(open.HeldOutStressed.Net.Profit), Ui.Signed(closed.HeldOutStressed.Net.Profit)],
			],
			1, 2);

		Ui.Blank();
		Ui.Figures(
			("windows up", $"{closed.PositiveWindows} of {closed.WalkForwardReturns.Count}"),
			("asked of the open data", result.TimesMeasuredOnOpenData.ToString(CultureInfo.InvariantCulture) + " time(s)"),
			("candidates before it", result.EarlierCandidates.ToString(CultureInfo.InvariantCulture)));

		Ui.Blank();
		Ui.Aside("  That slice is spent now. Nothing measured on it afterwards was chosen in ignorance of it.");
		Ui.Aside("  If this is the strategy, say so: odysseus complete \"why you believe it\"");
		Ui.Blank();

		return 0;
	}

	private static async Task<int> Complete(Workspace workspace, string[] args)
	{
		if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
		{
			Ui.Refuse("Say why you consider it finished: odysseus complete \"held up on the closed slice\"");

			return 1;
		}

		var project = workspace.Current();
		var candidate = workspace.CurrentCandidate();

		var completion = await workspace.Completions.CompleteAsync(
			project, candidate, string.Join(' ', args), Actors.User, CancellationToken.None);

		var kept = completion.Strategy;

		Ui.Title("complete", kept.Name);
		Ui.Blank();

		Ui.Figures(
			("class", kept.ClassName),
			("symbol", kept.Symbol),
			("candle", kept.TimeFrame.ToString()),
			("runs kept", kept.Runs.Count.ToString(CultureInfo.InvariantCulture)));

		Ui.Blank();
		Ui.Table(
			["kept", "what it is"],
			[
				[$"{kept.ClassName}.cs", "the strategy, as it was compiled and run"],
				["spec.json", "the hypothesis it was translated from"],
				["strategy.json", "the instrument, the numbers, the data and every run"],
			]);

		Ui.Blank();
		Ui.Say("  " + completion.Folder);
		Ui.Blank();
		Ui.Aside("  \"" + kept.Notes + "\"");
		Ui.Blank();

		return 0;
	}

	private static async Task<int> CompletedList(Workspace workspace)
	{
		var project = workspace.Current();
		var found = await workspace.Completions.ListAsync(project, CancellationToken.None);

		Ui.Title("completed", workspace.Completions.FolderOf(project));

		if (found.Count == 0)
		{
			Ui.Blank();
			Ui.Aside("  Nothing finished yet in this project.");
			Ui.Blank();

			return 0;
		}

		Ui.Blank();
		Ui.Table(
			["strategy", "symbol", "on data it did not see", "trades", "when", "why"],
			[.. found.Select(s => new[]
			{
				s.ClassName,
				s.Symbol,
				Ui.Signed(s.OnClosedData.HeldOut.Net.Profit),
				s.OnClosedData.HeldOut.Trades.Count.ToString(CultureInfo.InvariantCulture),
				s.CompletedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				Short(s.Notes),
			})],
			2, 3);

		Ui.Blank();

		return 0;
	}

	// A reason can be a paragraph; a column cannot.
	private static string Short(string text)
		=> text.Length <= 64 ? text : text[..63].TrimEnd() + "…";

	// The same four blocks the tool returns, laid out for a person instead of a parser.
	private static void Report(Measurement measurement)
	{
		Ui.Table(
			["", "formed on", "held out", "held out, heavier costs"],
			[
				[
					"profit",
					Ui.Signed(measurement.Development.Net.Profit),
					Ui.Signed(measurement.HeldOut.Net.Profit),
					Ui.Signed(measurement.HeldOutStressed.Net.Profit),
				],
				[
					"return",
					Ui.Signed(measurement.Development.Net.ReturnPercent) + " %",
					Ui.Signed(measurement.HeldOut.Net.ReturnPercent) + " %",
					Ui.Signed(measurement.HeldOutStressed.Net.ReturnPercent) + " %",
				],
				[
					"deepest fall",
					Ui.Number(measurement.Development.Risk.MaxDrawdownPercent, 2) + " %",
					Ui.Number(measurement.HeldOut.Risk.MaxDrawdownPercent, 2) + " %",
					Ui.Number(measurement.HeldOutStressed.Risk.MaxDrawdownPercent, 2) + " %",
				],
				[
					"trades",
					measurement.Development.Trades.Count.ToString(CultureInfo.InvariantCulture),
					measurement.HeldOut.Trades.Count.ToString(CultureInfo.InvariantCulture),
					measurement.HeldOutStressed.Trades.Count.ToString(CultureInfo.InvariantCulture),
				],
				[
					"won",
					Ui.Number(measurement.Development.Trades.WinRatePercent, 1) + " %",
					Ui.Number(measurement.HeldOut.Trades.WinRatePercent, 1) + " %",
					Ui.Number(measurement.HeldOutStressed.Trades.WinRatePercent, 1) + " %",
				],
			],
			1, 2, 3);

		Ui.Blank();
		Ui.Say("  in consecutive stretches of the part it was formed on");
		Ui.Blank();

		Ui.Table(
			["stretch", "return"],
			[.. measurement.WalkForwardReturns.Select((r, i) =>
				new[] { (i + 1).ToString(CultureInfo.InvariantCulture), Ui.Signed(r) + " %" })],
			1);

		Ui.Blank();
		Ui.Figures(
			("windows up", $"{measurement.PositiveWindows} of {measurement.WalkForwardReturns.Count}"),
			("spread between them", Ui.Number(measurement.WalkForwardSpread, 2) + " %"),
			("return per unit of fall", measurement.ReturnOverDrawdown is { } ratio
				? Ui.Number(ratio, 2)
				: "no fall to weigh it against"),
			("what heavier costs left", measurement.CostResilience is { } share
				? Ui.Number(share * 100m, 0) + " %"
				: "nothing to keep"));
	}

	private static async Task<int> Explain(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var run = args.Length > 0 ? RunId.Parse(args[0]) : workspace.CurrentRun();

		var breakdown = await workspace.Backtests.ExplainAsync(project, run, CancellationToken.None);

		Ui.Title("explain", run.Value);

		Cut("month by month", breakdown.ByMonth);
		Cut("by part of the session", breakdown.ByPartOfSession);
		Cut("by how long it was held", breakdown.ByHoldingTime);
		Cut("by direction", breakdown.ByDirection);

		Ui.Blank();
		Ui.Figures(
			("without its best month", Ui.Signed(breakdown.NetWithoutBestMonth)),
			("months traded", breakdown.MonthsTraded.ToString(CultureInfo.InvariantCulture)),
			("of those, in profit", breakdown.MonthsInProfit.ToString(CultureInfo.InvariantCulture)));

		Ui.Blank();
		Ui.Aside("  Every cut is of the same trades, so each adds back up to the run.");
		Ui.Blank();

		return 0;
	}

	private static void Cut(string title, IReadOnlyList<RunSegment> segments)
	{
		if (segments.Count == 0)
			return;

		Ui.Blank();
		Ui.Say("  " + title);
		Ui.Blank();

		Ui.Table(
			["", "trades", "result", "won", "share"],
			[.. segments.Select(s => new[]
			{
				s.Name,
				s.Trades.ToString(CultureInfo.InvariantCulture),
				Ui.Signed(s.Net),
				Ui.Number(s.WinRatePercent, 1) + " %",
				s.ShareOfNetPercent == 0 ? "" : Ui.Signed(s.ShareOfNetPercent) + " %",
			})],
			1, 2, 3, 4);
	}

	private static async Task<int> Runs(Workspace workspace, string[] args)
	{
		var project = workspace.Current();
		var runs = await workspace.Backtests.ListAsync(project, default, CancellationToken.None);

		Ui.Title("runs", $"{runs.Count} recorded");
		Ui.Blank();

		if (runs.Count == 0)
		{
			Ui.Aside("  Nothing yet.");
			Ui.Blank();

			return 0;
		}

		Ui.Table(
			["id", "slice", "symbol", "costs", "trades", "net", "finished"],
			[.. runs.Select(r => new[]
			{
				r.Id.Value,
				r.Slice.ToString(),
				r.Symbol,
				r.Scenario,
				r.Metrics?.Trades.Count.ToString(CultureInfo.InvariantCulture) ?? "—",
				r.Metrics is null ? "—" : Ui.Signed(r.Metrics.Net.Profit),
				r.FinishedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
			})],
			4, 5);

		Ui.Blank();

		return 0;
	}

	/// <summary>
	/// Every runner this projects root knows about.
	/// </summary>
	/// <param name="workspace">Everything a command needs.</param>
	/// <returns>Process exit code.</returns>
	/// <remarks>
	/// The registry is a directory on this machine, so this finds deployments the MCP server started as
	/// readily as ones started here. That is the point of it: a strategy that outlives the session that
	/// started it has to be findable by somebody who was not there.
	/// </remarks>
	private static async Task<int> Runners(Workspace workspace)
	{
		var runners = await workspace.Runners.ListAsync(CancellationToken.None);

		Ui.Title("runners", workspace.RunnerRegistry.Directory);
		Ui.Blank();

		if (runners.Count == 0)
		{
			Ui.Aside("  Nothing is running.");
			Ui.Blank();

			return 0;
		}

		Ui.Table(
			["deployment", "mode", "found", "pid", "position", "trades", "project"],
			[.. runners.Select(r => new[]
			{
				r.DeploymentId,
				r.Mode.ToString(),
				r.Status.ToString(),
				r.ProcessId.ToString(CultureInfo.InvariantCulture),
				r.State is null ? "—" : Ui.Number(r.State.Position),
				r.State is null ? "—" : r.State.Trades.ToString(CultureInfo.InvariantCulture),
				r.ProjectId,
			})],
			3, 4, 5);

		Ui.Blank();

		foreach (var runner in runners.Where(r => r.Status != RunnerStatuses.Attached))
			Ui.Aside($"  {runner.DeploymentId}: {runner.Detail}");

		Ui.Blank();

		return 0;
	}

	/// <summary>
	/// Stops or kills one runner.
	/// </summary>
	/// <param name="workspace">Everything a command needs.</param>
	/// <param name="terminal">Where a person can be asked something, and which says whether there is one.</param>
	/// <param name="args">The verb, the deployment, and whether to close the position.</param>
	/// <returns>Process exit code.</returns>
	private static async Task<int> Runner(Workspace workspace, IOperatorTerminal terminal, string[] args)
	{
		if (args.Length < 2)
		{
			Ui.Refuse("odysseus runner stop <deploymentId> [--close], or odysseus runner kill <deploymentId>.");

			return 1;
		}

		return args[0] switch
		{
			"stop" => await Stop(workspace, args[1], args[2..]),
			"kill" => Kill(workspace, terminal, args[1], args[2..]),
			_ => Unknown($"runner {args[0]}"),
		};
	}

	private static async Task<int> Stop(Workspace workspace, string deploymentId, string[] options)
	{
		var closePosition = options is ["--close"];

		if (options.Length > 0 && !closePosition)
			return Extra(options[0], "odysseus runner stop <deploymentId> [--close].");

		var ended = await workspace.Runners.StopAsync(deploymentId, closePosition, CancellationToken.None);

		Ui.Title(deploymentId, ended.Status.ToString());
		Ui.Blank();

		Ui.Figures(
			("mode", ended.Mode.ToString()),
			("position", ended.State is null ? "unknown" : Ui.Number(ended.State.Position)),
			("trades", ended.State?.Trades.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
			("realized", ended.State is null ? "unknown" : Ui.Signed(ended.State.RealizedProfit)));

		Ui.Blank();
		Ui.Aside("  " + ended.Detail);
		Ui.Blank();

		// The row in the project database is written by whichever process owns the project, and this
		// command does not open it. What was stopped is the process; what a session reads afterwards is
		// the row, and stop_deployment is what brings the two together.
		Ui.Aside("  The deployment's own record is updated by the server or by stop_deployment, not here.");
		Ui.Blank();

		// Nothing was stopped unless something answered. A person scripts against the exit code, and a
		// zero for a deployment that was already gone - or for a process that answered to the name and is
		// not this deployment - would report a position dealt with that nothing ever reached.
		return ended.Status == RunnerStatuses.Attached ? 0 : 1;
	}

	/// <summary>
	/// Ends the process itself, having said what it is holding.
	/// </summary>
	/// <param name="workspace">Everything a command needs.</param>
	/// <param name="terminal">Where the person is asked, and which says whether there is one.</param>
	/// <param name="deploymentId">Deployment whose process to end.</param>
	/// <param name="options">Anything else that was typed, which this command takes none of.</param>
	/// <returns>Process exit code.</returns>
	/// <remarks>
	/// A person's, at a terminal, and nowhere else: no tool can reach this, because a tool that could
	/// would be a way to leave a position with nothing watching it. It decides nothing about the position
	/// - killing is not a stop - so what it prints first is what is about to be left open.
	/// </remarks>
	private static int Kill(Workspace workspace, IOperatorTerminal terminal, string deploymentId, string[] options)
	{
		if (options.Length > 0)
			return Extra(options[0], "odysseus runner kill <deploymentId>.");

		var record = workspace.RunnerRegistry.Read(deploymentId);

		if (record is null)
		{
			Ui.Refuse($"There is no runner recorded for {deploymentId} under {workspace.RunnerRegistry.Directory}.");

			return 1;
		}

		var last = workspace.RunnerRegistry.Home(deploymentId).LastEntry();

		Ui.Title(deploymentId, $"{record.Mode} - process {record.ProcessId}");
		Ui.Blank();

		Ui.Figures(
			("symbol", record.Symbol),
			("position", last is null ? "unknown" : Ui.Number(last.Position)),
			("working orders", last?.WorkingOrders.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
			("account", record.Account is { Length: > 0 } account ? account : "unknown"));

		Ui.Blank();
		Ui.Aside("  Killing decides nothing about the position. Whatever it is holding stays open at the");
		Ui.Aside("  broker, and its working orders stay working until the venue's own rule ends them.");
		Ui.Blank();

		if (!terminal.IsInteractive)
		{
			Ui.Refuse("This needs a person at a terminal, and standard input here is not one.");

			return 1;
		}

		var typed = terminal.Ask(
			$"  Type the deployment identifier to end process {record.ProcessId}: ", CancellationToken.None);

		if (!string.Equals(typed?.Trim(), deploymentId, StringComparison.Ordinal))
		{
			Ui.Blank();
			Ui.Refuse("Nothing was killed.");

			return 1;
		}

		try
		{
			using var process = Process.GetProcessById(record.ProcessId);

			process.Kill(entireProcessTree: true);
		}
		catch (Exception error) when (error is ArgumentException or InvalidOperationException)
		{
			Ui.Blank();
			Ui.Refuse($"There is no process {record.ProcessId} any more.");

			return 1;
		}

		Ui.Blank();
		Ui.Say($"  Process {record.ProcessId} was ended. Its record and journal are in {workspace.RunnerRegistry.Home(deploymentId).Directory}.");
		Ui.Blank();

		return 0;
	}

	/// <summary>
	/// Prints an example live mandate.
	/// </summary>
	/// <param name="args">The verb.</param>
	/// <returns>Process exit code.</returns>
	/// <remarks>
	/// Printed, never written. A file this software can create is a file this software can be talked into
	/// creating, and the phrase inside it only means anything because no program here has ever read one.
	///
	/// The example goes to standard output and the explanation of it to standard error, so that
	/// redirecting the output into a file produces a mandate that parses while the person still reads
	/// what the phrase in it is going to be used for.
	/// </remarks>
	private static int Mandate(string[] args)
	{
		if (args.Length == 0 || args[0] != "template")
		{
			Ui.Refuse("odysseus mandate template");

			return 1;
		}

		Con.WriteLine(LiveMandateFile.Template());

		Con.Error.WriteLine();
		Con.Error.WriteLine(LiveMandateFile.Guidance());

		return 0;
	}

	// A count that came out zero or negative asks the broker for a window that runs backwards, and what
	// comes back from one of those is not a shorter history but a wrong one.
	private static int Days(string text)
		=> int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
			? days
			: throw new ArgumentException($"'{text}' is not a number of days: odysseus import NVDA 90 5m");

	private static TimeSpan Frame(string text)
		=> text switch
		{
			"1m" => TimeSpan.FromMinutes(1),
			"5m" => TimeSpan.FromMinutes(5),
			"15m" => TimeSpan.FromMinutes(15),
			"1h" => TimeSpan.FromHours(1),
			"1d" => TimeSpan.FromDays(1),
			_ => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var frame) && frame > TimeSpan.Zero
				? frame
				: throw new ArgumentException(
					$"'{text}' is not a candle length: 1m, 5m, 15m, 1h, 1d, or a time such as 00:05:00."),
		};
}
