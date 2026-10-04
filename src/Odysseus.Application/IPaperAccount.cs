namespace Odysseus.Application;

/// <summary>
/// What the broker says about the account itself.
/// </summary>
/// <param name="Name">
/// What the broker calls the account. The only field here that identifies whose money this is, which is
/// why a live mandate is checked against it before anything is started: a credential file pointed at the
/// wrong account is otherwise invisible until the first fill.
/// </param>
/// <param name="IsAccountActive">Whether the account is active.</param>
/// <param name="IsTradingBlocked">Whether the broker is currently refusing trades on it.</param>
/// <param name="BuyingPower">What it can buy with, in the account currency.</param>
/// <param name="Currency">Currency the figures are in.</param>
public sealed record AccountSummary(
	string Name,
	bool IsAccountActive,
	bool IsTradingBlocked,
	decimal BuyingPower,
	string Currency);

/// <summary>
/// One holding, as the broker reports it.
/// </summary>
/// <param name="Symbol">Instrument held.</param>
/// <param name="Quantity">How much, signed: negative is short.</param>
/// <param name="AverageEntryPrice">What it averaged to get in.</param>
/// <param name="MarketValue">What it is worth now.</param>
/// <param name="UnrealizedProfit">What it is up or down, when the broker says.</param>
public sealed record AccountPosition(
	string Symbol,
	decimal Quantity,
	decimal AverageEntryPrice,
	decimal MarketValue,
	decimal? UnrealizedProfit);

/// <summary>
/// One order still live at the broker.
/// </summary>
/// <param name="ClientOrderId">Identifier the order was sent under.</param>
/// <param name="BrokerOrderId">Identifier the broker gave it.</param>
/// <param name="Symbol">Instrument it is for.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="OrderType">Market, limit and so on.</param>
/// <param name="Quantity">How much was asked for.</param>
/// <param name="FilledQuantity">How much has been filled.</param>
/// <param name="LimitPrice">The limit, when it has one.</param>
/// <param name="TimeInForce">How long it stands.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="SubmittedAt">When it was sent, in UTC.</param>
/// <param name="FilledAveragePrice">What the fills averaged, when there are any.</param>
public sealed record AccountOrder(
	string ClientOrderId,
	string BrokerOrderId,
	string Symbol,
	string Side,
	string OrderType,
	decimal Quantity,
	decimal FilledQuantity,
	decimal? LimitPrice,
	string TimeInForce,
	string Status,
	DateTime SubmittedAt,
	decimal? FilledAveragePrice);

/// <summary>
/// What the account held at one moment.
/// </summary>
/// <param name="ObservedAt">When it was read, in UTC.</param>
/// <param name="Broker">Which account it was read from.</param>
/// <param name="Account">The account itself.</param>
/// <param name="Positions">What it is holding.</param>
/// <param name="Orders">What it has live.</param>
public sealed record PaperAccountState(
	DateTime ObservedAt,
	string Broker,
	AccountSummary Account,
	IReadOnlyList<AccountPosition> Positions,
	IReadOnlyList<AccountOrder> Orders);

/// <summary>
/// Reading the paper account, as opposed to running something on it.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPaperTrader"/> because it answers a different question, and because for a
/// long time nothing could answer it at all: every figure an agent could see came from what this
/// product's own deployment recorded, so a deployment left holding stock by a server that died read as
/// a flat position forever. The tools even told the caller to go and check the account - and there was
/// nothing to check it with.
///
/// It only ever reads. Deciding what to do about what it finds is not the server's, and closing a
/// position on somebody's behalf least of all.
/// </remarks>
public interface IPaperAccount
{
	/// <summary>
	/// Reads what the account holds now.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The account, its holdings and its live orders.</returns>
	ValueTask<PaperAccountState> ReadAsync(CancellationToken cancellationToken);
}
