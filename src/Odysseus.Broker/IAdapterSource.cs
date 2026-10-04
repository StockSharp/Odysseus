namespace Odysseus.Broker;

using Odysseus.Platform;

/// <summary>
/// Makes the adapter this server is to talk to the venue through.
/// </summary>
/// <remarks>
/// The whole of what used to be broker-specific, in one method. Everything above it - the download, the
/// lookup, the paper session - is written against the platform and does not know which venue answers.
/// </remarks>
internal interface IAdapterSource
{
	/// <summary>The identity a dataset records for bars that came through it.</summary>
	string SourceName { get; }

	/// <summary>Which market a symbol is quoted on.</summary>
	SymbolBoards Boards { get; }

	/// <summary>
	/// Builds a configured adapter: credentials applied, paper asserted, settings applied.
	/// </summary>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	/// <returns>The adapter, ready to connect.</returns>
	IMessageAdapter Create(IdGenerator ids);
}

/// <summary>
/// The adapter of the connector that was selected.
/// </summary>
internal sealed class ConnectorAdapterSource : IAdapterSource
{
	private readonly Type _adapter;
	private readonly BrokerCredentials _credentials;
	private readonly IReadOnlyDictionary<string, string> _settings;
	private readonly TradingMandate _mandate;

	/// <summary>
	/// Creates the source.
	/// </summary>
	/// <param name="adapter">Adapter type out of the connector's package.</param>
	/// <param name="credentials">What the credential file held, or null when there was none.</param>
	/// <param name="settings">Settings by property name, with the reserved keys already removed.</param>
	/// <param name="boards">Which market a symbol is quoted on.</param>
	/// <param name="sourceName">The identity a dataset records.</param>
	/// <param name="mandate">Which account this process may reach, as its start-up configuration decided.</param>
	public ConnectorAdapterSource(
		Type adapter,
		BrokerCredentials credentials,
		IReadOnlyDictionary<string, string> settings,
		SymbolBoards boards,
		string sourceName,
		TradingMandate mandate)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

		_adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
		_credentials = credentials;
		_settings = settings ?? new Dictionary<string, string>();
		_mandate = mandate ?? throw new ArgumentNullException(nameof(mandate));

		Boards = boards ?? throw new ArgumentNullException(nameof(boards));
		SourceName = sourceName;
	}

	/// <inheritdoc />
	public string SourceName { get; }

	/// <inheritdoc />
	public SymbolBoards Boards { get; }

	/// <inheritdoc />
	public IMessageAdapter Create(IdGenerator ids)
	{
		var adapter = AdapterCatalog.Create(_adapter, ids);

		try
		{
			AdapterConfigurator.Apply(adapter, _credentials, _settings, _mandate);
		}
		catch
		{
			adapter.Dispose();
			throw;
		}

		return adapter;
	}
}
