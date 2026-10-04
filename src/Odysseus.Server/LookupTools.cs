namespace Odysseus.Server;

using System.Globalization;

/// <summary>
/// The tools that say what there is to trade.
/// </summary>
[McpServerToolType]
public static class LookupTools
{
	/// <summary>
	/// Finds instruments the broker offers.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="lookup">What asks the broker.</param>
	/// <param name="query">Part of a code or a name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="limit">Most to return.</param>
	/// <returns>What was found.</returns>
	[McpServerTool(Name = "lookup_symbols")]
	[Description("Find instruments the broker offers, by part of a code or a name. Use it before " +
		"import_history rather than guessing a symbol: a code the account cannot hold is discovered here " +
		"in one call, or at the end of a download that returns nothing. It reads the broker and imports " +
		"nothing.")]
	public static Task<object> LookupSymbols(
		ToolGuard guard,
		ISecurityLookup lookup,
		[Description("Part of a code or a name, for example 'NVDA'.")] string query,
		CancellationToken cancellationToken,
		[Description("Most to return. Zero asks for fifty.")] int limit = 0)
		=> guard.RunAsync(nameof(LookupSymbols), async () =>
		{
			var found = await lookup.SearchAsync(
				new(query, Underlying: string.Empty, ExpiringFrom: null, ExpiringTo: null, Limit: Size(limit)),
				cancellationToken);

			return new
			{
				symbols = found.Select(Describe).ToArray(),
				returned = found.Count,
			};
		});

	/// <summary>
	/// Lists the option contracts written on an instrument.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="lookup">What asks the broker.</param>
	/// <param name="underlying">Instrument the contracts are written on.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="expiringFrom">Earliest expiry, as yyyy-MM-dd, or empty for no lower bound.</param>
	/// <param name="expiringTo">Latest expiry, as yyyy-MM-dd, or empty for no upper bound.</param>
	/// <param name="limit">Most to return.</param>
	/// <returns>The contracts.</returns>
	[McpServerTool(Name = "lookup_option_contracts")]
	[Description("List the option contracts the broker offers on an instrument, optionally within a " +
		"window of expiries. An option is traded here by its full contract code, which carries the " +
		"underlying, the expiry, the right and the strike in nineteen characters that have to be exactly " +
		"right - so this is where one comes from. Everything else in this product treats such a code like " +
		"any other symbol: the same specification, the same runs, the same measurements, with the hundred " +
		"shares a contract carries taken into account when the result is worked out.")]
	public static Task<object> LookupOptionContracts(
		ToolGuard guard,
		ISecurityLookup lookup,
		[Description("Code of the instrument the contracts are written on, for example 'NVDA'.")] string underlying,
		CancellationToken cancellationToken,
		[Description("Earliest expiry to include, as yyyy-MM-dd. Leave empty for no lower bound.")] string expiringFrom = "",
		[Description("Latest expiry to include, as yyyy-MM-dd. Leave empty for no upper bound.")] string expiringTo = "",
		[Description("Most to return. Zero asks for fifty.")] int limit = 0)
		=> guard.RunAsync(nameof(LookupOptionContracts), async () =>
		{
			var found = await lookup.SearchAsync(
				new(
					Text: string.Empty,
					Underlying: underlying,
					ExpiringFrom: Moment(expiringFrom, nameof(expiringFrom)),
					ExpiringTo: Moment(expiringTo, nameof(expiringTo)),
					Limit: Size(limit)),
				cancellationToken);

			return new
			{
				underlying,
				contracts = found.Select(Describe).ToArray(),
				returned = found.Count,
				howToRead = "The contract code is what to pass wherever this product asks for a symbol. " +
					"One contract carries a hundred shares, and every result is worked out with that in it.",
			};
		});

	private static int Size(int limit) => limit <= 0 ? Page.DefaultSize : Math.Min(limit, Page.MaximumSize);

	private static DateTime? Moment(string value, string argument)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;

		if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
			out var moment))
		{
			throw new ArgumentException($"'{value}' is not a date. Write it as yyyy-MM-dd.", argument);
		}

		return moment;
	}

	private static object Describe(FoundSecurity security)
		=> new
		{
			symbol = security.Symbol,
			name = security.Name,
			board = security.Board,
			kind = security.Kind,
			underlying = security.Underlying,
			expiry = security.Expiry,
			strike = security.Strike,
			optionType = security.OptionType,
			contractSize = security.ContractSize,
		};
}
