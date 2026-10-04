namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using Odysseus.Domain;

/// <summary>
/// Permission to trade real money, as an operator wrote it down.
/// </summary>
/// <param name="Mode">Which of the two accounts this permits.</param>
/// <param name="Phrase">
/// The sentence a human must type back before a live runner starts, demanded by
/// <see cref="LiveMandateConfirmation"/> through a channel this file cannot reach. It lives in a file no
/// agent reads, so it cannot be dictated into a transcript for a tired person to paste.
/// </param>
/// <param name="Account">The account identity the broker must report, so a mispointed credential file is caught.</param>
/// <param name="PackageId">Connector package this permission is for, and only this one.</param>
/// <param name="PackageVersion">Exact version of that package.</param>
/// <param name="AdapterTypeName">Full type name of the adapter inside it.</param>
/// <param name="Symbols">The instruments it may trade.</param>
/// <param name="MaxPositionNotional">Largest position, at the last price, this permits.</param>
/// <param name="ExpiresAt">When it stops working, in UTC.</param>
/// <param name="Origin">Where it was read from, so a state report can say what is authorising it.</param>
/// <remarks>
/// A mandate is for one connector build, one account and a handful of symbols, and it expires. All four
/// narrowings exist for the same reason: a permission that outlives the intention behind it is the one
/// an accident uses.
/// </remarks>
public sealed record TradingMandate(
	TradingModes Mode,
	string Phrase,
	string Account,
	string PackageId,
	string PackageVersion,
	string AdapterTypeName,
	IReadOnlyList<string> Symbols,
	decimal MaxPositionNotional,
	DateTime ExpiresAt,
	string Origin)
{
	/// <summary>The absence of a mandate, which is what every process has unless an operator said otherwise.</summary>
	public static TradingMandate Paper { get; } = new(
		TradingModes.Paper,
		string.Empty,
		string.Empty,
		string.Empty,
		string.Empty,
		string.Empty,
		[],
		0m,
		DateTime.MaxValue,
		"no mandate");

	/// <summary>Whether this permits real money.</summary>
	public bool IsLive => Mode == TradingModes.Live;

	/// <summary>
	/// Whether the permission has run out.
	/// </summary>
	/// <param name="utcNow">The current moment, in UTC.</param>
	/// <returns><see langword="true"/> when it has.</returns>
	public bool IsExpired(DateTime utcNow) => IsLive && utcNow >= ExpiresAt;

	/// <summary>
	/// Whether an instrument is one this permits.
	/// </summary>
	/// <param name="symbol">Instrument to check.</param>
	/// <returns><see langword="true"/> when the mandate names it, and always on paper.</returns>
	public bool Allows(string symbol)
		=> !IsLive || Symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Raised when a live mandate was asked for and what came back cannot be trusted.
/// </summary>
/// <remarks>
/// Never a fall back to paper. An operator who set the variable meant a real account, and running their
/// strategy against a demo one instead would be safe and dishonest: the difference would sit in a field
/// nobody re-reads while the experiment quietly measured the wrong thing. Refusing to start is the
/// honest failure.
/// </remarks>
public sealed class LiveMandateInvalidException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What is wrong with the mandate, and where it was read from.</param>
	public LiveMandateInvalidException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// Creates the exception over an underlying failure.
	/// </summary>
	/// <param name="message">What is wrong with the mandate, and where it was read from.</param>
	/// <param name="inner">What went wrong underneath.</param>
	public LiveMandateInvalidException(string message, Exception inner)
		: base(message, inner)
	{
	}
}

/// <summary>
/// The one reader of the live mandate file.
/// </summary>
/// <remarks>
/// Nothing in this product ever writes one. Not a tool, not the command line, not a template command
/// that offers to create it: a file the software can write is a file the software can be talked into
/// writing. The command line prints an example to standard output for a human to save, with
/// <see cref="Guidance"/> beside it on standard error, and that is the whole of the help it gives.
///
/// The variable is read once, at start-up, in the process that is going to trade. It is not a request
/// argument, it does not cross the runner protocol, and the MCP server removes it from the environment
/// of every child it starts - so a runner cannot inherit live mode from the shell that happened to
/// launch the server.
///
/// Reading the file is half of the arrangement. What is written in it has to be confirmed by a person
/// too, which is <see cref="LiveMandateConfirmation"/>: this says what was authorised, and that says
/// somebody read it.
/// </remarks>
public static class LiveMandateFile
{
	/// <summary>Environment variable naming the mandate file.</summary>
	public const string PathVariable = "ODYSSEUS_LIVE_MANDATE";

	/// <summary>Schema version this reader understands.</summary>
	public const int Schema = 1;

	/// <summary>
	/// An example of the file, for a human to save and edit. Printing one is the whole of the help this
	/// product gives about arranging a live mandate.
	/// </summary>
	/// <returns>The example, as JSON.</returns>
	public static string Template()
		=> """
			{
			  "schema": 1,
			  "phrase": "trade real money on U1234567 until the thirtieth",
			  "account": "U1234567",
			  "connector": {
			    "packageId": "StockSharp.Example",
			    "packageVersion": "1.2.3",
			    "adapter": "StockSharp.Example.ExampleMessageAdapter"
			  },
			  "symbols": ["AAPL"],
			  "maxPositionNotional": 5000,
			  "expiresAt": "2026-09-30T00:00:00Z"
			}
			""";

	/// <summary>
	/// What the example does not say by itself: what to do with the file, and what will be asked of the
	/// phrase in it.
	/// </summary>
	/// <returns>The explanation, as prose meant for a person rather than for a parser.</returns>
	/// <remarks>
	/// Kept apart from <see cref="Template"/> so the example stays a file that parses. The command line
	/// prints this on standard error and the example on standard output, so that redirecting the output
	/// into a file still produces a mandate and the person still reads why the phrase is there.
	///
	/// A gate nobody explained is an obstacle, and an obstacle is something people learn to get past
	/// rather than something they think about.
	/// </remarks>
	public static string Guidance()
		=> $"""
			Save this where only you can write it, point {PathVariable} at it, and start Odysseus.Runner
			yourself, giving it the runner home to work out of.

			The phrase is the second gate, and it is the one this file cannot pass on its own. Started at a
			terminal, the runner prints what it is about to trade and asks for the phrase: type it back
			exactly - the spacing and the capitalisation as written here - and nothing trades until you
			have. Where there is no terminal to ask at, put the same phrase in {LiveMandateConfirmation.PhraseVariable} instead.
			A phrase that does not match, or none at all, starts nothing: there is no fall back to paper,
			because running a real experiment on a demo account is the safe kind of lie.

			So write a sentence, not a password. Somebody typing it back should be saying what they are about
			to do, in the words of whoever authorised it - which is what makes holding this file different
			from having meant it.
			""";

	/// <summary>
	/// Reads the mandate a path names, against the current moment.
	/// </summary>
	/// <param name="path">Path of the file, or null when the variable said nothing.</param>
	/// <returns>The mandate, or <see cref="TradingMandate.Paper"/> when no path was given.</returns>
	/// <exception cref="LiveMandateInvalidException">A path was given and what is at it cannot be trusted.</exception>
	public static TradingMandate Read(string path) => Read(path, DateTime.UtcNow);

	/// <summary>
	/// Reads the mandate a path names.
	/// </summary>
	/// <param name="path">Path of the file, or null when the variable said nothing.</param>
	/// <param name="utcNow">The current moment, in UTC, which decides whether it has expired.</param>
	/// <returns>The mandate, or <see cref="TradingMandate.Paper"/> when no path was given.</returns>
	/// <exception cref="LiveMandateInvalidException">A path was given and what is at it cannot be trusted.</exception>
	/// <remarks>
	/// Paper is returned for exactly one reason: nobody named a file. Every other outcome - the file is
	/// not there, it will not parse, a field is missing, it has expired - is a refusal.
	/// </remarks>
	public static TradingMandate Read(string path, DateTime utcNow)
	{
		if (string.IsNullOrWhiteSpace(path))
			return TradingMandate.Paper;

		var full = Path.GetFullPath(path);

		if (!File.Exists(full))
		{
			throw new LiveMandateInvalidException(
				$"{PathVariable} names '{full}' and there is no file there. A process told to trade real " +
				"money starts nothing until the permission to do so can be read.");
		}

		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(File.ReadAllBytes(full));
		}
		catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
		{
			throw new LiveMandateInvalidException($"The mandate at '{full}' could not be read: {error.Message}", error);
		}

		using (document)
		{
			var root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object)
				throw new LiveMandateInvalidException($"The mandate at '{full}' is not an object.");

			var schema = Number(root, "schema", full);

			if (schema != Schema)
			{
				throw new LiveMandateInvalidException(
					$"The mandate at '{full}' says schema {schema} and this build reads {Schema}.");
			}

			var connector = Object(root, "connector", full);

			var mandate = new TradingMandate(
				TradingModes.Live,
				Text(root, "phrase", full),
				Text(root, "account", full),
				Text(connector, "packageId", full),
				Text(connector, "packageVersion", full),
				Text(connector, "adapter", full),
				Symbols(root, full),
				Number(root, "maxPositionNotional", full),
				Moment(root, "expiresAt", full),
				full);

			if (mandate.MaxPositionNotional <= 0m)
			{
				throw new LiveMandateInvalidException(
					$"The mandate at '{full}' caps the position at {mandate.MaxPositionNotional}, which permits nothing.");
			}

			if (mandate.IsExpired(utcNow))
			{
				throw new LiveMandateInvalidException(
					$"The mandate at '{full}' expired at {mandate.ExpiresAt:O} and it is now {utcNow:O}. " +
					"Mandates expire so that a permission nobody remembered to revoke stops working by itself.");
			}

			return mandate;
		}
	}

	private static JsonElement Object(JsonElement root, string name, string path)
	{
		if (!root.TryGetProperty(name, out var found) || found.ValueKind != JsonValueKind.Object)
			throw new LiveMandateInvalidException($"The mandate at '{path}' has no '{name}' object.");

		return found;
	}

	private static string Text(JsonElement parent, string name, string path)
	{
		if (!parent.TryGetProperty(name, out var found) || found.ValueKind != JsonValueKind.String)
			throw new LiveMandateInvalidException($"The mandate at '{path}' has no '{name}'.");

		var value = found.GetString();

		if (string.IsNullOrWhiteSpace(value))
			throw new LiveMandateInvalidException($"The mandate at '{path}' leaves '{name}' empty.");

		return value;
	}

	private static decimal Number(JsonElement parent, string name, string path)
	{
		if (!parent.TryGetProperty(name, out var found) || found.ValueKind != JsonValueKind.Number)
			throw new LiveMandateInvalidException($"The mandate at '{path}' has no numeric '{name}'.");

		return found.GetDecimal();
	}

	private static DateTime Moment(JsonElement parent, string name, string path)
	{
		var text = Text(parent, name, path);

		if (!DateTime.TryParse(
			text,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
			out var moment))
		{
			throw new LiveMandateInvalidException($"The mandate at '{path}' has '{name}' as '{text}', which is not a moment.");
		}

		return DateTime.SpecifyKind(moment, DateTimeKind.Utc);
	}

	private static IReadOnlyList<string> Symbols(JsonElement root, string path)
	{
		if (!root.TryGetProperty("symbols", out var found) || found.ValueKind != JsonValueKind.Array)
			throw new LiveMandateInvalidException($"The mandate at '{path}' has no 'symbols' array.");

		var symbols = new List<string>();

		foreach (var element in found.EnumerateArray())
		{
			if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
				throw new LiveMandateInvalidException($"The mandate at '{path}' lists a symbol that is not a name.");

			symbols.Add(element.GetString());
		}

		if (symbols.Count == 0)
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{path}' names no symbols, which permits nothing. A mandate is for the " +
				"instruments somebody meant, not for whatever is asked for.");
		}

		return symbols;
	}
}
