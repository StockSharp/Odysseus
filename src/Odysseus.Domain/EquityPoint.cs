namespace Odysseus.Domain;

/// <summary>
/// The account value at one moment of a run.
/// </summary>
/// <param name="Time">Moment the value was taken, in UTC.</param>
/// <param name="Equity">Account value including any open position, in the account currency.</param>
public readonly record struct EquityPoint(DateTime Time, decimal Equity);
