namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// The broker ports this server answers itself, and the connector a deployment is told to load.
/// </summary>
/// <remarks>
/// One object registered for every port, because one connector owns one connection and two objects would
/// open two of them. Everything that holds a broker port holds this, so choosing a connector while the
/// server runs changes no signature anywhere and no service is rebuilt.
///
/// Trading is not among the ports any more. A deployment runs in a process of its own that loads its own
/// connector, so what this supplies for one is a name rather than a connection - and the account this
/// server reads through <see cref="IPaperAccount"/> is this server's, which need not be the account a
/// runner is trading on.
///
/// Nothing is bound until something binds it, and until then every port refuses with
/// <see cref="BrokerNotConfiguredException"/> naming what was being attempted. That is what the
/// stand-in it replaces used to do, and for the same reason: registering the ports only when there is
/// a broker looks tidier and is worse, because the tools that hold them then fail while their arguments
/// are being bound, before any code of ours runs, and the agent is told only that something went wrong
/// invoking a tool - with no category to branch on and no mention of credentials anywhere.
/// </remarks>
public sealed class BrokerGateway : IHistorySource, IPaperAccount, ISecurityLookup, IDeploymentConnector
{
	private readonly IConnectorFactory _factory;
	private readonly ConnectorChoice _named;
	private readonly Lock _sync = new();

	private BrokerBinding _binding;
	private ConnectorChoice _choice;

	/// <summary>
	/// Creates the gateway.
	/// </summary>
	/// <param name="factory">How a connector is loaded.</param>
	/// <param name="named">
	/// The connector the operator named at start-up, or null when none was. It is what a deployment falls
	/// back to when no session has selected one - a runner loads its own connector, so deploying needs a
	/// connector named rather than one bound here.
	/// </param>
	public BrokerGateway(IConnectorFactory factory, ConnectorChoice named)
	{
		_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		_named = named;
	}

	/// <summary>What is bound now, or <see langword="null"/> when nothing is.</summary>
	public ConnectorDescription Current
	{
		get
		{
			using (_sync.EnterScope())
				return _binding?.Connector;
		}
	}

	/// <summary>
	/// The choice a session made, or <see langword="null"/> when none has been made here.
	/// </summary>
	/// <remarks>
	/// Kept because what a runner has to be told is the choice and not the description: the description
	/// says what a connector turned out to be, and the settings that configure it are only on the choice.
	/// </remarks>
	public ConnectorChoice CurrentChoice
	{
		get
		{
			using (_sync.EnterScope())
				return _choice;
		}
	}

	/// <summary>Whether a connector has been chosen and loaded.</summary>
	public bool IsBound => Current is not null;

	/// <summary>How a connector is loaded, so a tool can inspect one without binding it.</summary>
	public IConnectorFactory Connectors => _factory;

	/// <summary>
	/// Loads a connector and binds it to the four ports.
	/// </summary>
	/// <param name="choice">Which connector, and how it is configured.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was bound.</returns>
	/// <remarks>
	/// The download and the load happen outside the lock, because they take seconds and reach the
	/// network; only the swap is held, so a call already in flight through the previous connector
	/// finishes against the binding it started with.
	/// </remarks>
	public async ValueTask<ConnectorDescription> SelectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(choice);

		var binding = await _factory.OpenAsync(choice, cancellationToken);

		using (_sync.EnterScope())
		{
			_binding = binding;
			_choice = choice;
		}

		return binding.Connector;
	}

	/// <inheritdoc />
	/// <remarks>
	/// Whatever a session selected, and failing that whatever the operator named at start-up. Neither has
	/// to be loaded here: the process that trades loads its own, and this server refuses only when nothing
	/// at all has named one.
	/// </remarks>
	public ConnectorChoice Choose()
		=> CurrentChoice
			?? _named
			?? throw new BrokerNotConfiguredException(
				"Starting a deployment, which names a connector for the process that will trade it to load,");

	/// <inheritdoc />
	string IHistorySource.SourceName => Bound("Downloading history").History.SourceName;

	/// <inheritdoc />
	public Task<IReadOnlyList<Candle>> GetBarsAsync(
		string symbol,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken)
		=> Bound("Downloading history").History.GetBarsAsync(symbol, timeFrame, from, to, cancellationToken);

	/// <inheritdoc />
	public ValueTask<PaperAccountState> ReadAsync(CancellationToken cancellationToken)
		=> Bound("Reading the account this server bound").Account.ReadAsync(cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<FoundSecurity>> SearchAsync(
		SecurityQuery query,
		CancellationToken cancellationToken)
		=> Bound("Asking what instruments exist").Lookup.SearchAsync(query, cancellationToken);

	private BrokerBinding Bound(string what)
	{
		using (_sync.EnterScope())
			return _binding ?? throw new BrokerNotConfiguredException(what);
	}
}
