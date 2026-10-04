namespace Odysseus.Application.Tests;

using System.Threading;

/// <summary>
/// A remote StockSharp storage server as the place history is imported from instead of a broker.
/// </summary>
[TestClass]
public class RemoteStorageTests : OdysseusTestBase
{
	private static readonly DateTime _from = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	/// <summary>The file names the server and, optionally, the account to sign in with.</summary>
	[TestMethod]
	public void AFileNamesTheServerAndTheAccount()
	{
		var choice = RemoteStorageFile.Parse(
			"""{ "address": "10.0.0.5:5002", "login": "research", "password": "shhh" }""",
			"the test");

		AreEqual("10.0.0.5:5002", choice.Address);
		AreEqual("research", choice.Login);
		AreEqual("shhh", choice.Password);
	}

	/// <summary>A server that lets anybody read needs no account.</summary>
	[TestMethod]
	public void AnAccountIsOptional()
	{
		var choice = RemoteStorageFile.Parse("""{ "address": "storage.local:5002" }""", "the test");

		AreEqual("storage.local:5002", choice.Address);
		AreEqual(string.Empty, choice.Login);
		AreEqual(string.Empty, choice.Password);
	}

	/// <summary>A file that names no server is refused, and says what it is missing.</summary>
	[TestMethod]
	public void AFileWithNoAddressIsRefused()
	{
		var refusal = Throws<ConnectorRefusedException>(() => RemoteStorageFile.Parse("""{ "login": "x" }""", "the test"));

		IsTrue(refusal.Message.Contains("address", StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>No file named, no remote storage: history comes from the broker.</summary>
	[TestMethod]
	public void NoFileMeansNoRemoteStorage()
		=> IsNull(RemoteStorageFile.Read(null));

	/// <summary>The password never prints itself, wherever the choice ends up being logged.</summary>
	[TestMethod]
	public void ThePasswordDoesNotPrintItself()
	{
		var choice = new RemoteStorageChoice("storage.local:5002", "research", "shhh");

		IsFalse(choice.ToString().Contains("shhh", StringComparison.Ordinal), choice.ToString());
	}

	/// <summary>A dataset imported from the server says so, by the server's address.</summary>
	[TestMethod]
	public void TheSourceIsNamedAfterTheServer()
	{
		var source = new RemoteStorageSource(new Factory([]), new("storage.local:5002", "", ""));

		AreEqual("storage:storage.local:5002", source.SourceName);
	}

	/// <summary>The server is connected once, on the first download, and every download reads through it.</summary>
	[TestMethod]
	public async Task TheServerIsOpenedOnceAndReadThrough()
	{
		var bars = new List<Candle> { new(_from, 100m, 101m, 99m, 100.5m, 1_000m) };
		var factory = new Factory(bars);
		var source = new RemoteStorageSource(factory, new("storage.local:5002", "", ""));

		var first = await source.GetBarsAsync("NVDA", TimeSpan.FromMinutes(5), _from, _from.AddDays(1), CancellationToken);
		var second = await source.GetBarsAsync("AMD", TimeSpan.FromMinutes(5), _from, _from.AddDays(1), CancellationToken);

		AreEqual(1, factory.Opened, "the server was connected more than once.");
		AreEqual(1, first.Count);
		AreEqual(1, second.Count);
	}

	private sealed class Factory(IReadOnlyList<Candle> bars) : IConnectorFactory
	{
		public int Opened { get; private set; }

		public IReadOnlyList<string> Allowed => ["StockSharp."];

		public IReadOnlyList<string> Sources => ["https://example.invalid/index.json"];

		public ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult<IReadOnlyList<ConnectorDescription>>([]);

		public ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken)
		{
			Opened++;

			return ValueTask.FromResult<IHistorySource>(new Storage(bars));
		}
	}

	private sealed class Storage(IReadOnlyList<Candle> bars) : IHistorySource
	{
		public string SourceName => "storage";

		public Task<IReadOnlyList<Candle>> GetBarsAsync(
			string symbol,
			TimeSpan timeFrame,
			DateTime from,
			DateTime to,
			CancellationToken cancellationToken)
			=> Task.FromResult(bars);
	}
}
