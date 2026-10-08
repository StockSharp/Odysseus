namespace StockSharp.Odysseus.Broker;

using System.Threading;
using System.Threading.Tasks;

using StockSharp.Odysseus.Domain;

/// <summary>
/// Asks the broker what it offers.
/// </summary>
/// <remarks>
/// The same adapter the history comes through, asked a different question, and closed again when it has
/// answered. Nothing here holds a connection: a lookup is something a person does a few times while
/// deciding what to research, not something the product does while it runs.
/// </remarks>
internal sealed class StockSharpSecurityLookup : ISecurityLookup
{
	private readonly IAdapterSource _adapters;

	/// <summary>
	/// Creates the lookup.
	/// </summary>
	/// <param name="adapters">Where the configured adapter comes from.</param>
	public StockSharpSecurityLookup(IAdapterSource adapters)
	{
		_adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<FoundSecurity>> SearchAsync(
		SecurityQuery query,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		using var adapter = _adapters.Create(new IncrementalIdGenerator());

		var boards = _adapters.Boards;
		var wanted = query.Underlying.IsEmpty() ? null : query.Underlying.Trim().ToUpperInvariant();

		var request = new SecurityLookupMessage
		{
			SecurityId = new()
			{
				SecurityCode = query.Text.IsEmpty() ? "*" : query.Text.Trim().ToUpperInvariant(),
				BoardCode = wanted is null ? boards.Of(query.Text ?? string.Empty) : boards.Option,
			},
			UnderlyingSecurityId = wanted is null ? default : new() { SecurityCode = wanted, BoardCode = boards.Equity },
			SecurityTypes = wanted is null ? null : [SecurityTypes.Option],
			ExpiryDate = query.ExpiringFrom,
			Count = query.Limit > 0 ? query.Limit : null,
		};

		var found = new List<FoundSecurity>();

		await foreach (var message in adapter
			.ConnectAndDownloadAsync<SecurityMessage>(request)
			.WithCancellation(cancellationToken))
		{
			var symbol = message.SecurityId.SecurityCode;

			if (symbol.IsEmpty())
				continue;

			var expiry = message.ExpiryDate is { } when_ ? DateTime.SpecifyKind(when_, DateTimeKind.Utc) : (DateTime?)null;

			// The broker answers with the whole chain and the caller asked for a window of it, so the
			// window is applied here rather than pretended to have been asked for.
			if (query.ExpiringFrom is { } after && expiry is { } e1 && e1 < after)
				continue;

			if (query.ExpiringTo is { } before && expiry is { } e2 && e2 > before)
				continue;

			found.Add(new(
				symbol,
				message.Name ?? string.Empty,
				message.SecurityId.BoardCode ?? string.Empty,
				message.SecurityType?.ToString() ?? string.Empty,
				message.UnderlyingSecurityId.SecurityCode ?? string.Empty,
				expiry,
				message.Strike,
				message.OptionType?.ToString() ?? string.Empty,
				ContractSymbol.SizeOf(symbol)));

			if (query.Limit > 0 && found.Count >= query.Limit)
				break;
		}

		return found;
	}
}
