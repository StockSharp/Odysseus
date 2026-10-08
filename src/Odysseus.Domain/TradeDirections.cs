namespace StockSharp.Odysseus.Domain;

/// <summary>
/// Which way a position is held.
/// </summary>
public enum TradeDirections
{
	/// <summary>Bought to open, sold to close.</summary>
	Long,

	/// <summary>Sold to open, bought to close.</summary>
	Short,
}
