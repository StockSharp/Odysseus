namespace Odysseus.Platform;

using System;

using Ecng.IO;

using StockSharp.Algo.Storages;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

using Odysseus.Domain;

/// <summary>
/// What a run and a search both need before either can start.
/// </summary>
/// <remarks>
/// A backtest and a parameter search set up the same world - one instrument, one simulated account,
/// the bars of one range, and a strategy built out of a compiled candidate. Only what they do
/// with it differs, so the setting up lives here and neither owns a private copy of it that could
/// drift.
/// </remarks>
internal static class EmulationSetup
{
	/// <summary>
	/// The identity bars are stored and looked up under.
	/// </summary>
	/// <param name="symbol">Symbol the bars belong to.</param>
	/// <returns>The security identifier.</returns>
	/// <remarks>
	/// Where bars are written and what the emulator asks for have to be the same thing, and they are
	/// only reliably the same thing if one of them is built out of the other. The instrument below takes
	/// its identifier from here for that reason.
	/// </remarks>
	public static SecurityId SecurityIdOf(string symbol)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		return new() { SecurityCode = symbol, BoardCode = ExchangeBoard.Nasdaq.Code };
	}

	/// <summary>
	/// The instrument the run trades.
	/// </summary>
	/// <param name="symbol">Symbol to trade.</param>
	/// <param name="priceStep">Smallest price movement.</param>
	/// <returns>The security.</returns>
	public static Security CreateSecurity(string symbol, decimal priceStep)
	{
		var contract = ContractSymbol.IsContract(symbol);

		// One board for both, because what the emulator reads from it is the trading day, and a listed
		// contract keeps the hours of the market underneath it.
		return new()
		{
			Id = SecurityIdOf(symbol).ToStringId(),
			Code = symbol,
			Board = ExchangeBoard.Nasdaq,
			PriceStep = priceStep,
			VolumeStep = 1m,

			// A contract is a hundred shares, so a cent of price is a dollar of result. Left at one, the
			// engine's own account value disagrees with every trade measured out of the same run.
			Multiplier = ContractSymbol.SizeOf(symbol),
			Type = contract ? SecurityTypes.Option : SecurityTypes.Stock,
		};
	}

	/// <summary>
	/// The simulated account the run trades on.
	/// </summary>
	/// <param name="startingEquity">Money the account starts with.</param>
	/// <returns>The portfolio.</returns>
	public static Portfolio CreatePortfolio(decimal startingEquity)
	{
		var portfolio = Portfolio.CreateSimulator();

		portfolio.BeginValue = startingEquity;
		portfolio.CurrentValue = startingEquity;

		return portfolio;
	}

	/// <summary>
	/// Opens the bars a run is measured over.
	/// </summary>
	/// <param name="folder">Folder of the shared market-data storage.</param>
	/// <returns>Storage over the bars that folder holds.</returns>
	/// <remarks>
	/// Read-only, because a market-data drive is a directory the engine is otherwise happy to append a day
	/// to. A run therefore leaves nothing behind, and a measurement the next measurement can change is not
	/// a measurement.
	/// </remarks>
	public static IStorageRegistry Open(string folder)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(folder);

		return new StorageRegistry
		{
			DefaultDrive = new LocalMarketDataDrive(new ReadOnlyFileSystem(LocalFileSystem.Instance), folder),
		};
	}
}
