namespace Odysseus.Domain;

/// <summary>
/// Result of a run on one side of the cost line.
/// </summary>
/// <param name="Profit">Profit over the run, in the account currency.</param>
/// <param name="ReturnPercent">Profit as a percent of the starting equity.</param>
/// <param name="ProfitFactor">
/// Winning results divided by the absolute value of the losing ones. Absent when the run had no losing
/// trade, because the ratio is then undefined rather than large.
/// </param>
/// <param name="AverageTrade">Average result of one closed trade, in the account currency.</param>
public sealed record ProfitBlock(
	decimal Profit,
	decimal ReturnPercent,
	decimal? ProfitFactor,
	decimal AverageTrade);

/// <summary>
/// What the run was charged for trading.
/// </summary>
/// <param name="Commission">Commission over the run, as a positive amount.</param>
/// <param name="Slippage">Modelled slippage over the run, as a positive amount.</param>
public sealed record CostBlock(decimal Commission, decimal Slippage)
{
	/// <summary>Commission and slippage together, which is the gap between the gross and net results.</summary>
	public decimal Total => Commission + Slippage;
}

/// <summary>
/// How far the run fell and whether it came back.
/// </summary>
/// <param name="MaxDrawdownPercent">Deepest peak-to-trough decline of the net equity curve, in percent.</param>
/// <param name="MaxDrawdownAmount">The same decline in the account currency, as a positive amount.</param>
/// <param name="RecoveryFactor">
/// Net profit over the deepest decline. Absent when the run never declined, because the ratio is then
/// undefined rather than zero.
/// </param>
public sealed record RiskBlock(
	decimal MaxDrawdownPercent,
	decimal MaxDrawdownAmount,
	decimal? RecoveryFactor);

/// <summary>
/// The trade population of the run.
/// </summary>
/// <param name="Count">Closed trades.</param>
/// <param name="WinRatePercent">Share of closed trades with a positive net result, in percent.</param>
/// <param name="AverageHoldingMinutes">Average holding time of a closed trade, in minutes.</param>
public sealed record TradeBlock(int Count, decimal WinRatePercent, decimal AverageHoldingMinutes);

/// <summary>
/// How much the run traded and how long it was exposed.
/// </summary>
/// <param name="Turnover">Money that changed hands, over entries and exits together.</param>
/// <param name="ExposurePercent">Share of the tradable time of the slice with a position open, in percent.</param>
public sealed record ActivityBlock(decimal Turnover, decimal ExposurePercent);

/// <summary>
/// How much of the result rests on one trade and on one symbol.
/// </summary>
/// <param name="LargestTradeProfitSharePercent">Share of the net profit contributed by the best single trade.</param>
/// <param name="LargestSymbolProfitSharePercent">Share of the net profit contributed by the best single symbol.</param>
/// <param name="LargestTradeId">The trade behind the largest trade share.</param>
/// <param name="LargestSymbol">The symbol behind the largest symbol share.</param>
public sealed record ConcentrationBlock(
	decimal LargestTradeProfitSharePercent,
	decimal LargestSymbolProfitSharePercent,
	string LargestTradeId,
	string LargestSymbol);

/// <summary>
/// Everything one run of one candidate measured.
/// </summary>
/// <param name="Gross">Result before commission.</param>
/// <param name="Net">Result after commission, which is the side that counts.</param>
/// <param name="Costs">What the run was charged.</param>
/// <param name="Risk">How far it fell.</param>
/// <param name="Trades">Its trade population.</param>
/// <param name="Activity">How much it traded.</param>
/// <param name="Concentration">How much of it rests on one trade.</param>
/// <param name="ExecutionErrorCount">Orders the run could not place or fill.</param>
public sealed record RunMetrics(
	ProfitBlock Gross,
	ProfitBlock Net,
	CostBlock Costs,
	RiskBlock Risk,
	TradeBlock Trades,
	ActivityBlock Activity,
	ConcentrationBlock Concentration,
	int ExecutionErrorCount);
