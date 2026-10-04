namespace Odysseus.Broker;

/// <summary>
/// Makes an adapter say it is pointed at the one real account a mandate names, and refuses it otherwise.
/// </summary>
/// <remarks>
/// The mirror image of <see cref="PaperOnlyGuard"/>, and deliberately shaped the same way so that
/// neither can drift from the other. The paper guard asserts the demo flag on; this one asserts it off.
/// Both require the adapter to implement the interface that carries the flag, because a connector that
/// cannot represent the difference between a demo account and a real one cannot be shown to be on
/// either - and that is a refusal in both directions, not only in the direction that spends money.
///
/// Its assertions are positive rather than permissive. A mandate is written for one connector build,
/// one adapter type and one account; anything else is a different arrangement than the one somebody
/// authorised, and being close to it is not being it.
///
/// Nothing here weakens the paper claim. This code is reachable only from a process whose mandate came
/// out of a file named by an environment variable read at start-up, which the MCP server removes from
/// the environment of every process it starts. On the server's own path <see cref="PaperOnlyGuard"/> is
/// still the only guard that runs.
/// </remarks>
internal static class LiveTradingGuard
{
	/// <summary>
	/// Checks that the package the factory actually loaded is the one the mandate was written for.
	/// </summary>
	/// <param name="packageId">Package the connector came out of.</param>
	/// <param name="packageVersion">Version that was loaded.</param>
	/// <param name="mandate">The permission being exercised.</param>
	/// <exception cref="ConnectorNotPaperException">The build differs from the one that was authorised.</exception>
	/// <remarks>
	/// Checked against what was loaded rather than against what was asked for. A choice naming no
	/// version resolves to whatever the sources offer today, and "whatever is newest" is not something
	/// a person authorised last week.
	/// </remarks>
	public static void AssertPackage(string packageId, string packageVersion, TradingMandate mandate)
	{
		ArgumentNullException.ThrowIfNull(mandate);

		if (!mandate.IsLive)
			return;

		if (!string.Equals(packageId, mandate.PackageId, StringComparison.OrdinalIgnoreCase))
		{
			throw new ConnectorNotPaperException(
				$"This process holds a mandate for '{mandate.PackageId}' and loaded '{packageId}'. A mandate is " +
				"for one connector, so nothing is started.");
		}

		if (!string.Equals(packageVersion, mandate.PackageVersion, StringComparison.OrdinalIgnoreCase))
		{
			throw new ConnectorNotPaperException(
				$"This process holds a mandate for {mandate.PackageId} {mandate.PackageVersion} and loaded " +
				$"{packageVersion}. A mandate is for one build of one connector, so nothing is started.");
		}
	}

	/// <summary>
	/// Puts an adapter onto the real account the mandate names, or refuses it.
	/// </summary>
	/// <param name="adapter">The adapter to check.</param>
	/// <param name="mandate">The permission being exercised.</param>
	/// <exception cref="ConnectorNotPaperException">It is not the adapter the mandate names, or it will not come off demo.</exception>
	public static void Assert(IMessageAdapter adapter, TradingMandate mandate)
	{
		ArgumentNullException.ThrowIfNull(adapter);
		ArgumentNullException.ThrowIfNull(mandate);

		var name = adapter.GetType().FullName;

		if (!string.Equals(name, mandate.AdapterTypeName, StringComparison.Ordinal))
		{
			throw new ConnectorNotPaperException(
				$"This process holds a mandate for '{mandate.AdapterTypeName}' and was handed '{name}'. " +
				"Nothing is started.");
		}

		var demo = Demo(adapter);

		demo.IsDemo = false;

		if (demo.IsDemo)
		{
			throw new ConnectorNotPaperException(
				$"{name} would not come out of demo mode, so it cannot reach the account the mandate names.");
		}
	}

	/// <summary>
	/// Checks that an adapter is still on the real account, without touching the flag.
	/// </summary>
	/// <param name="adapter">The adapter to check.</param>
	/// <exception cref="ConnectorNotPaperException">A setting put it back onto a demo account.</exception>
	/// <remarks>
	/// Read rather than written, for the reason the paper guard gives: assigning the flag again would
	/// re-run whatever side effects it has over the settings that were just applied. A live runner that
	/// was quietly moved onto a demo account is refused rather than quietly moved back.
	/// </remarks>
	public static void Confirm(IMessageAdapter adapter)
	{
		ArgumentNullException.ThrowIfNull(adapter);

		if (Demo(adapter).IsDemo)
		{
			throw new ConnectorNotPaperException(
				$"{adapter.GetType().FullName} came out of configuration in demo mode although this process " +
				"holds a mandate for a real account. A state report would then say it is trading money it is " +
				"not, so nothing is started.");
		}
	}

	private static IDemoAdapter Demo(IMessageAdapter adapter)
	{
		if (adapter is IDemoAdapter demo)
			return demo;

		throw new ConnectorNotPaperException(
			$"{adapter.GetType().FullName} has no way of saying whether it is on a demo account, so it cannot " +
			"be shown to be on the real one the mandate names either. Both modes require the same interface, " +
			"because a connector that cannot represent the difference cannot be proven to be on either side of it.");
	}
}
