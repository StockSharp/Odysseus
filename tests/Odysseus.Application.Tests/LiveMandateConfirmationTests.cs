namespace Odysseus.Application.Tests;

using System.Globalization;
using System.Threading;

/// <summary>
/// The second gate on real money: the phrase written in a mandate, demanded back through a channel the
/// mandate file cannot reach.
/// </summary>
/// <remarks>
/// Everything here rests on one sentence - holding the file is not the permission. A file can be copied,
/// inherited from an experiment months ago, or pointed at by a variable somebody exported once and never
/// took out of a shell profile, and in every one of those cases the phrase is still not returned by
/// anybody. So the answer has to come from a person at a terminal or from a second variable, it is
/// compared exactly, and neither a mismatch nor a silence ever turns into a quiet paper run.
/// </remarks>
[TestClass]
public class LiveMandateConfirmationTests : OdysseusTestBase
{
	private static readonly DateTime _now = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

	private const string Phrase = "trade real money on U1234567 until the thirtieth";

	/// <summary>A paper mandate permits no real account, so there is nobody to ask and nothing to ask for.</summary>
	[TestMethod]
	public void PaperAsksNobodyAnything()
	{
		var terminal = new Terminal(interactive: true, types: "whatever");

		new LiveMandateConfirmation(terminal, string.Empty).Assert(TradingMandate.Paper, CancellationToken);

		AreEqual(0, terminal.Questions.Count, "somebody was asked to confirm a mandate that permits nothing.");
	}

	/// <summary>At a terminal, the phrase has to be typed back before anything is permitted to start.</summary>
	[TestMethod]
	public void AtATerminalThePhraseHasToBeTypedBack()
	{
		var terminal = new Terminal(interactive: true, types: Phrase);

		new LiveMandateConfirmation(terminal, string.Empty).Assert(Mandate(), CancellationToken);

		AreEqual(1, terminal.Questions.Count, "a live runner started at a terminal without asking anybody anything.");
	}

	/// <summary>
	/// The question says what is about to be traded and never the phrase itself. Printing it would turn
	/// typing it back into copying it off the screen, which is the one thing this exists to prevent.
	/// </summary>
	[TestMethod]
	public void TheQuestionNeverPrintsThePhraseItIsAskingFor()
	{
		var terminal = new Terminal(interactive: true, types: Phrase);

		new LiveMandateConfirmation(terminal, string.Empty).Assert(Mandate(), CancellationToken);

		var question = terminal.Questions[0];

		IsFalse(question.Contains(Phrase, StringComparison.OrdinalIgnoreCase),
			$"the question printed the phrase it was asking for: {question}");

		IsTrue(question.Contains("U1234567", StringComparison.Ordinal), question);
		IsTrue(question.Contains("AAPL", StringComparison.Ordinal), question);
		IsTrue(question.Contains("/etc/odysseus/mandate.json", StringComparison.Ordinal), question);
	}

	/// <summary>A phrase typed wrong at a terminal starts nothing.</summary>
	[TestMethod]
	public void APhraseTypedWrongIsARefusal()
		=> Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: true, types: "trade real money"), string.Empty)
				.Assert(Mandate(), CancellationToken));

	/// <summary>
	/// Somebody who answers nothing at all - a bare return, or a standard input that ended - has confirmed
	/// nothing, and a bare return is exactly what a person gives a prompt they did not read.
	/// </summary>
	[TestMethod]
	public void AnsweringNothingAtAllIsARefusal()
	{
		Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: true, types: string.Empty), string.Empty)
				.Assert(Mandate(), CancellationToken));

		Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: true, types: null), string.Empty)
				.Assert(Mandate(), CancellationToken));
	}

	/// <summary>
	/// A question that was interrupted confirms nothing. Somebody who pressed the interrupt instead of
	/// typing has not said the sentence back, and there is nothing smaller to fall back to: the phrase
	/// sitting in the variable is not an answer either, because the person being asked is the point.
	/// </summary>
	[TestMethod]
	public void AQuestionThatWasInterruptedIsARefusal()
	{
		using var interrupted = new CancellationTokenSource();

		interrupted.Cancel();

		var terminal = new Terminal(interactive: true, types: Phrase);

		// The variable carries the right phrase, and it must not rescue a question nobody answered.
		var refusal = Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(terminal, Phrase).Assert(Mandate(), interrupted.Token));

		AreEqual(1, terminal.Questions.Count, "the person at the terminal was never asked at all.");

		IsTrue(refusal.Message.Contains("Nobody answered", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say that nobody answered: {refusal.Message}");

		IsTrue(refusal.Message.Contains("/etc/odysseus/mandate.json", StringComparison.Ordinal),
			$"the refusal does not say which mandate went unconfirmed: {refusal.Message}");
	}

	/// <summary>
	/// Nothing is trimmed into equality. A phrase with a stray space at either end is a different phrase,
	/// because a comparison that forgave one would be forgiving whatever else it was told to.
	/// </summary>
	[TestMethod]
	public void SpacingIsPartOfThePhrase()
	{
		var typed = new[]
		{
			" " + Phrase,
			Phrase + " ",
			Phrase + "\t",
			Phrase.Replace(" ", "  ", StringComparison.Ordinal),
		};

		foreach (var answer in typed)
		{
			Throws<LiveMandateInvalidException>(
				() => new LiveMandateConfirmation(new Terminal(interactive: true, types: answer), string.Empty)
					.Assert(Mandate(), CancellationToken),
				$"'{answer}' was trimmed or squeezed into a match.");
		}
	}

	/// <summary>Capitalisation is part of the phrase too.</summary>
	[TestMethod]
	public void CapitalisationIsPartOfThePhrase()
		=> Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(
					new Terminal(interactive: true, types: Phrase.ToUpperInvariant()), string.Empty)
				.Assert(Mandate(), CancellationToken));

	/// <summary>
	/// The comparison does not depend on the language the machine is set to. Under a culture's rules "ß"
	/// and "ss" are the same string, and a Turkish machine disagrees with an English one about "I", so a
	/// gate compared that way would open on one machine and not on another.
	/// </summary>
	[TestMethod]
	public void TheComparisonDoesNotDependOnTheMachinesLanguage()
	{
		var was = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

			var mandate = Mandate() with { Phrase = "trade on the ISTANBUL straße account" };

			var typed = new[]
			{
				"trade on the ıSTANBUL straße account",
				"trade on the ISTANBUL strasse account",
			};

			foreach (var answer in typed)
			{
				Throws<LiveMandateInvalidException>(
					() => new LiveMandateConfirmation(new Terminal(interactive: true, types: answer), string.Empty)
						.Assert(mandate, CancellationToken),
					$"'{answer}' matched under this machine's culture, which is not a gate.");
			}

			new LiveMandateConfirmation(new Terminal(interactive: true, types: mandate.Phrase), string.Empty)
				.Assert(mandate, CancellationToken);
		}
		finally
		{
			CultureInfo.CurrentCulture = was;
		}
	}

	/// <summary>
	/// With nobody to ask, the phrase comes from the variable instead - and from nowhere else, because a
	/// process with no terminal must never sit waiting for somebody who is not there.
	/// </summary>
	[TestMethod]
	public void WithNoTerminalThePhraseComesFromTheVariable()
	{
		var terminal = new Terminal(interactive: false, types: Phrase);

		new LiveMandateConfirmation(terminal, Phrase).Assert(Mandate(), CancellationToken);

		AreEqual(0, terminal.Questions.Count, "a process with nobody at it stopped to ask a question.");
	}

	/// <summary>A variable that carries the wrong phrase starts nothing, and the refusal names the variable.</summary>
	[TestMethod]
	public void AVariableThatCarriesTheWrongPhraseIsARefusal()
	{
		var refusal = Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: false, types: null), "trade real money")
				.Assert(Mandate(), CancellationToken));

		IsTrue(refusal.Message.Contains(LiveMandateConfirmation.PhraseVariable, StringComparison.Ordinal),
			$"the refusal does not name the variable it read: {refusal.Message}");
	}

	/// <summary>
	/// A variable nobody set is the ordinary case - a mandate file that was inherited, copied or exported
	/// once and forgotten - and it is a refusal naming what would fix it, not a fall back to paper.
	/// </summary>
	[TestMethod]
	public void AVariableNobodySetIsARefusalThatSaysWhatWouldFixIt()
	{
		foreach (var offered in new[] { null, string.Empty })
		{
			var refusal = Throws<LiveMandateInvalidException>(
				() => new LiveMandateConfirmation(new Terminal(interactive: false, types: null), offered)
					.Assert(Mandate(), CancellationToken));

			IsTrue(refusal.Message.Contains(LiveMandateConfirmation.PhraseVariable, StringComparison.Ordinal),
				$"the refusal does not name the variable that would fix it: {refusal.Message}");
		}
	}

	/// <summary>
	/// Spacing and capitalisation count in the variable exactly as they do at a terminal: it is the same
	/// comparison, reached by a different channel.
	/// </summary>
	[TestMethod]
	public void TheVariableIsComparedTheSameWayTheTypedAnswerIs()
	{
		var offered = new[]
		{
			Phrase + " ",
			Phrase.ToUpperInvariant(),
			Phrase.Replace(" ", "", StringComparison.Ordinal),
		};

		foreach (var value in offered)
		{
			Throws<LiveMandateInvalidException>(
				() => new LiveMandateConfirmation(new Terminal(interactive: false, types: null), value)
					.Assert(Mandate(), CancellationToken),
				$"'{value}' was accepted from the variable although it is not the phrase.");
		}
	}

	/// <summary>
	/// A live mandate with no phrase in it can never be confirmed. The file reader refuses an empty one,
	/// and this refuses it again rather than letting an empty answer confirm an empty phrase.
	/// </summary>
	[TestMethod]
	public void ALiveMandateWithNoPhraseCanNeverBeConfirmed()
	{
		var mandate = Mandate() with { Phrase = string.Empty };

		Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: true, types: string.Empty), string.Empty)
				.Assert(mandate, CancellationToken));

		Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: false, types: null), string.Empty)
				.Assert(mandate, CancellationToken));
	}

	/// <summary>
	/// Every refusal names the mandate it could not confirm, whichever channel was supposed to confirm
	/// it: a person reading the log has to be able to tell which permission went unused and why.
	/// </summary>
	[TestMethod]
	public void EveryRefusalNamesTheMandateItCouldNotConfirm()
	{
		var typed = Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: true, types: "no"), string.Empty)
				.Assert(Mandate(), CancellationToken));

		var offered = Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: false, types: null), "no")
				.Assert(Mandate(), CancellationToken));

		var absent = Throws<LiveMandateInvalidException>(
			() => new LiveMandateConfirmation(new Terminal(interactive: false, types: null), null)
				.Assert(Mandate(), CancellationToken));

		foreach (var refusal in new[] { typed, offered, absent })
		{
			IsTrue(refusal.Message.Contains("/etc/odysseus/mandate.json", StringComparison.Ordinal),
				$"the refusal does not say which mandate went unconfirmed: {refusal.Message}");
		}
	}

	private static TradingMandate Mandate()
		=> new(
			TradingModes.Live,
			Phrase,
			"U1234567",
			"StockSharp.Example",
			"1.2.3",
			"StockSharp.Example.ExampleMessageAdapter",
			["AAPL"],
			5_000m,
			_now.AddDays(26),
			"/etc/odysseus/mandate.json");

	/// <summary>A person who types whatever the test needs them to have typed, or nobody at all.</summary>
	private sealed class Terminal(bool interactive, string types) : IOperatorTerminal
	{
		public List<string> Questions { get; } = [];

		public bool IsInteractive => interactive;

		public string Ask(string question, CancellationToken cancellationToken)
		{
			if (!interactive)
				throw new InvalidOperationException("a terminal with nobody at it was asked a question.");

			Questions.Add(question);

			// A question that has been given up on is one nobody typed anything into, which is what a
			// real terminal reports when an interrupt arrives while the question is standing.
			return cancellationToken.IsCancellationRequested ? null : types;
		}
	}
}
