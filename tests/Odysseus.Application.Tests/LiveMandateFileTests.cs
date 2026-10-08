namespace StockSharp.Odysseus.Application.Tests;

/// <summary>
/// The one file that permits real money, and the one reader of it.
/// </summary>
/// <remarks>
/// Everything here turns on a single asymmetry. Paper is returned for exactly one reason - nobody named
/// a file - and every other outcome is a refusal. The tempting alternative, falling back to paper when
/// the mandate cannot be read, is safe and dishonest: it would run a strategy an operator meant for a
/// real account against a demo one, and say so in a field nobody re-reads while the experiment quietly
/// measured the wrong thing.
///
/// Nothing in this product writes one of these. The command line prints an example for a person to save,
/// and that is the whole of the help it gives: a file the software can create is a file the software can
/// be talked into creating, and the phrase inside only means anything because no program here reads one.
/// </remarks>
[TestClass]
public class LiveMandateFileTests : OdysseusTestBase
{
	private static readonly DateTime _now = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

	private string _directory;

	private static string Valid =>
		"""
		{
		  "schema": 1,
		  "phrase": "trade real money on U1234567 until the thirtieth",
		  "account": "U1234567",
		  "connector": {
		    "packageId": "StockSharp.Example",
		    "packageVersion": "1.2.3",
		    "adapter": "StockSharp.Example.ExampleMessageAdapter"
		  },
		  "symbols": ["AAPL"],
		  "maxPositionNotional": 5000,
		  "expiresAt": "2026-09-30T00:00:00Z"
		}
		""";

	/// <summary>Makes a directory of its own.</summary>
	[TestInitialize]
	public void CreateDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), "odysseus-mandates", Guid.NewGuid().ToString("n"));

		Directory.CreateDirectory(_directory);
	}

	/// <summary>Removes it.</summary>
	[TestCleanup]
	public void DeleteDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	/// <summary>Naming no file is the one thing that means paper, and it is what every process gets by default.</summary>
	[TestMethod]
	public void NamingNoFileMeansPaper()
	{
		AreEqual(TradingModes.Paper, LiveMandateFile.Read(null, _now).Mode);
		AreEqual(TradingModes.Paper, LiveMandateFile.Read(string.Empty, _now).Mode);
		AreEqual(TradingModes.Paper, LiveMandateFile.Read("   ", _now).Mode);

		IsFalse(TradingMandate.Paper.IsLive);
	}

	/// <summary>A mandate that reads is the whole of what was written down, and it permits exactly that.</summary>
	[TestMethod]
	public void AMandateThatReadsPermitsExactlyWhatItNames()
	{
		var mandate = LiveMandateFile.Read(Write(Valid), _now);

		AreEqual(TradingModes.Live, mandate.Mode);
		IsTrue(mandate.IsLive);
		AreEqual("trade real money on U1234567 until the thirtieth", mandate.Phrase);
		AreEqual("U1234567", mandate.Account);
		AreEqual("StockSharp.Example", mandate.PackageId);
		AreEqual("1.2.3", mandate.PackageVersion);
		AreEqual("StockSharp.Example.ExampleMessageAdapter", mandate.AdapterTypeName);
		AreEqual(5_000m, mandate.MaxPositionNotional);
		AreEqual(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), mandate.ExpiresAt);

		IsTrue(mandate.Allows("AAPL"));
		IsFalse(mandate.Allows("MSFT"), "a mandate permitted an instrument it does not name.");
	}

	/// <summary>
	/// A file that was named and is not there is a refusal, not a quiet fall back to paper. An operator
	/// who set the variable meant a real account, and running against a demo one instead is the dishonest
	/// half of safe.
	/// </summary>
	[TestMethod]
	public void AMissingFileIsARefusalRatherThanPaper()
	{
		var missing = Path.Combine(_directory, "not-there.json");

		var refusal = Throws<LiveMandateInvalidException>(() => LiveMandateFile.Read(missing, _now));

		IsTrue(refusal.Message.Contains(LiveMandateFile.PathVariable, StringComparison.Ordinal),
			$"the refusal does not name the variable that pointed at nothing: {refusal.Message}");
	}

	/// <summary>A file that will not parse is a refusal too.</summary>
	[TestMethod]
	public void AFileThatWillNotParseIsARefusal()
		=> Throws<LiveMandateInvalidException>(() => LiveMandateFile.Read(Write("{ \"schema\": 1, \"phra"), _now));

	/// <summary>
	/// Every field is required, because each one narrows what the permission covers and a missing one
	/// would silently widen it.
	/// </summary>
	[TestMethod]
	public void EveryFieldIsRequired()
	{
		foreach (var field in new[] { "phrase", "account", "symbols", "maxPositionNotional", "expiresAt" })
		{
			var refusal = Throws<LiveMandateInvalidException>(() => LiveMandateFile.Read(Write(Without(field)), _now));

			IsTrue(refusal.Message.Contains(field, StringComparison.OrdinalIgnoreCase),
				$"a mandate missing '{field}' was refused without saying which field was missing: {refusal.Message}");
		}
	}

	/// <summary>A mandate that names no instruments permits nothing, and is refused rather than read as such.</summary>
	[TestMethod]
	public void AMandateThatNamesNoInstrumentsIsRefused()
	{
		var refusal = Throws<LiveMandateInvalidException>(
			() => LiveMandateFile.Read(Write(Valid.Replace("[\"AAPL\"]", "[]", StringComparison.Ordinal)), _now));

		IsTrue(refusal.Message.Contains("symbols", StringComparison.OrdinalIgnoreCase), refusal.Message);
	}

	/// <summary>A cap of nothing permits nothing, and saying so is better than starting under it.</summary>
	[TestMethod]
	public void ACapOfNothingIsRefused()
		=> Throws<LiveMandateInvalidException>(
			() => LiveMandateFile.Read(Write(Valid.Replace("5000", "0", StringComparison.Ordinal)), _now));

	/// <summary>
	/// Mandates expire, so a permission nobody remembered to revoke stops working by itself - and an
	/// expired one refuses rather than quietly becoming a paper run of somebody's real experiment.
	/// </summary>
	[TestMethod]
	public void AnExpiredMandateIsRefused()
	{
		var path = Write(Valid);

		IsTrue(LiveMandateFile.Read(path, _now).IsLive);

		var refusal = Throws<LiveMandateInvalidException>(
			() => LiveMandateFile.Read(path, new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));

		IsTrue(refusal.Message.Contains("expired", StringComparison.OrdinalIgnoreCase), refusal.Message);
	}

	/// <summary>A mandate this build does not understand is refused rather than read as far as it goes.</summary>
	[TestMethod]
	public void AMandateOfAnotherSchemaIsRefused()
	{
		var refusal = Throws<LiveMandateInvalidException>(
			() => LiveMandateFile.Read(Write(Valid.Replace("\"schema\": 1", "\"schema\": 2", StringComparison.Ordinal)), _now));

		IsTrue(refusal.Message.Contains("schema", StringComparison.OrdinalIgnoreCase), refusal.Message);
	}

	/// <summary>
	/// The example the command line prints is one this reader accepts, so a person following it does not
	/// discover a typo at the moment they wanted to start trading.
	/// </summary>
	[TestMethod]
	public void ThePrintedExampleIsOneThisReaderAccepts()
	{
		var mandate = LiveMandateFile.Read(Write(LiveMandateFile.Template()), _now);

		IsTrue(mandate.IsLive);
		IsTrue(mandate.Phrase.Length > 0);
		AreEqual(1, mandate.Symbols.Count);
	}

	/// <summary>
	/// The example cannot explain itself - it is a file that has to parse - so what is printed beside it
	/// says what the phrase in it is for and what will be asked of it. A gate nobody explained is only an
	/// obstacle, and an obstacle is a thing people learn to get past.
	/// </summary>
	[TestMethod]
	public void ThePrintedExampleSaysWhatThePhraseWillBeAskedFor()
	{
		var guidance = LiveMandateFile.Guidance();

		IsTrue(guidance.Contains(LiveMandateFile.PathVariable, StringComparison.Ordinal),
			$"the guidance does not say which variable names the file: {guidance}");

		IsTrue(guidance.Contains(LiveMandateConfirmation.PhraseVariable, StringComparison.Ordinal),
			$"the guidance does not say how to confirm the phrase where there is no terminal: {guidance}");

		IsTrue(guidance.Contains("type it back", StringComparison.OrdinalIgnoreCase),
			$"the guidance does not say the phrase has to be typed back: {guidance}");

		IsTrue(guidance.Contains("paper", StringComparison.OrdinalIgnoreCase),
			$"the guidance does not say what an unconfirmed phrase does instead: {guidance}");
	}

	/// <summary>A paper mandate expires never, and permits whatever it is asked - the guard is elsewhere.</summary>
	[TestMethod]
	public void PaperExpiresNever()
	{
		IsFalse(TradingMandate.Paper.IsExpired(DateTime.MaxValue));
		IsTrue(TradingMandate.Paper.Allows("anything at all"));
	}

	private static string Without(string field)
		=> Valid
			.Replace($"\"{field}\":", $"\"not-{field}\":", StringComparison.Ordinal);

	private string Write(string content)
	{
		var path = Path.Combine(_directory, $"{Guid.NewGuid():n}.json");

		File.WriteAllText(path, content);

		return path;
	}
}
