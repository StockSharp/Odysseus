namespace StockSharp.Odysseus.Broker;

/// <summary>
/// Makes an adapter say it is pointed at a paper venue, and refuses it when it cannot.
/// </summary>
/// <remarks>
/// This is the whole of the product's paper-only claim, and it is the one place that makes it. A
/// connector that has no way of being told it is on paper is refused rather than used carefully: there
/// is no careful, because the next order it sends is a real one and nothing downstream would look any
/// different.
///
/// The flag is asserted before the caller's settings are applied and checked again afterwards. That is
/// not belt and braces: setting a demo flag has side effects on some connectors - one of them switches
/// the market data feed to a single exchange as a consequence - so the settings must come second, and a
/// setting that turned the flag back off again must not be able to pass unnoticed.
///
/// Which is why the check afterwards is a check and not a second assertion. Assigning the flag again
/// would re-run those side effects over the settings the caller had just been promised, and would turn
/// a connector that quietly came off paper into one that was quietly put back on it.
/// </remarks>
internal static class PaperOnlyGuard
{
	/// <summary>
	/// Puts an adapter into demo mode, or refuses it.
	/// </summary>
	/// <param name="adapter">The adapter to check.</param>
	/// <exception cref="ConnectorNotPaperException">It cannot be told, or would not be told.</exception>
	public static void Assert(IMessageAdapter adapter)
	{
		var demo = Demo(adapter);

		demo.IsDemo = true;

		if (!demo.IsDemo)
		{
			throw new ConnectorNotPaperException(
				$"{adapter.GetType().FullName} refused to go into demo mode, so it would trade for real.");
		}
	}

	/// <summary>
	/// Checks that an adapter is still in demo mode, without touching the flag.
	/// </summary>
	/// <param name="adapter">The adapter to check.</param>
	/// <exception cref="ConnectorNotPaperException">It is no longer on a paper account.</exception>
	public static void Confirm(IMessageAdapter adapter)
	{
		if (!Demo(adapter).IsDemo)
		{
			throw new ConnectorNotPaperException(
				$"{adapter.GetType().FullName} came out of configuration no longer in demo mode, so a setting " +
				"took it off the paper account. Remove it: this server trades on paper only.");
		}
	}

	private static IDemoAdapter Demo(IMessageAdapter adapter)
	{
		if (adapter is IDemoAdapter demo)
			return demo;

		throw new ConnectorNotPaperException(
			$"{adapter?.GetType().FullName ?? "The connector"} has no way of being told it is on a paper " +
			"account, so this server will not use it. Choose a connector that offers a demo mode.");
	}
}
