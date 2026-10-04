namespace Odysseus.Application;

using System;
using System.Globalization;
using System.Threading;

/// <summary>
/// Demands the phrase written in a live mandate back through a channel that is not the mandate file.
/// </summary>
/// <remarks>
/// The mandate says what may be traded. This says that a person read it. Holding the file is deliberately
/// not the whole of the permission: a file can be copied, inherited from an experiment three months ago,
/// left behind by somebody who has since gone home, or pointed at by a variable that was exported once
/// and never taken out of a shell profile. So the sentence inside it has to come back from somewhere the
/// file cannot supply - typed at the terminal by whoever is starting the runner, or put in
/// <see cref="PhraseVariable"/> by whoever arranged a start nobody is sitting at.
///
/// Neither channel is a secret, and treating it as one would be the more dangerous mistake. The phrase
/// is in a file on the same machine, and anybody who can read that file can produce it; this gate is not
/// built to withstand somebody holding the operator's disk. It is built so that starting a live runner
/// cannot be something that merely happens to a person, without them saying once, in the words whoever
/// authorised it chose, that this is what they meant.
///
/// A phrase that does not match, and a confirmation that never arrives, both end the same way: a
/// <see cref="LiveMandateInvalidException"/>, which is what every other unusable mandate raises. There is
/// no fall back to paper here for the reason there is none anywhere else - it would run a strategy meant
/// for a real account against a demo one and record the difference in a field nobody re-reads.
/// </remarks>
public sealed class LiveMandateConfirmation
{
	/// <summary>
	/// Environment variable carrying the phrase, for a live start with no terminal to ask at.
	/// </summary>
	/// <remarks>
	/// The second half of a pair, and it travels the way the first half does: the host that launches
	/// runners removes both from the environment of every child it starts, so neither live mode nor the
	/// confirmation of it can be inherited from the shell that happened to start a server.
	/// </remarks>
	public const string PhraseVariable = "ODYSSEUS_LIVE_PHRASE";

	private readonly IOperatorTerminal _terminal;
	private readonly string _offered;

	/// <summary>
	/// Creates the confirmation.
	/// </summary>
	/// <param name="terminal">Where a person can be asked, and which says whether there is one.</param>
	/// <param name="offered">
	/// What <see cref="PhraseVariable"/> carried in this process, or empty when it carried nothing. Read
	/// by the process that is going to trade and passed in, the way the mandate path is: the environment
	/// is looked at once, at start-up, in one place.
	/// </param>
	public LiveMandateConfirmation(IOperatorTerminal terminal, string offered)
	{
		_terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
		_offered = offered ?? string.Empty;
	}

	/// <summary>
	/// Requires the phrase in a mandate to be returned before the mandate is acted on.
	/// </summary>
	/// <param name="mandate">The permission being exercised.</param>
	/// <param name="cancellationToken">
	/// What ends the wait for an answer: an interrupt at the terminal, or this process being stopped.
	/// A question that has been given up on is refused exactly like one nobody answered, because that
	/// is what it is.
	/// </param>
	/// <exception cref="LiveMandateInvalidException">
	/// Nothing was returned, or what was returned is not the phrase. Never a downgrade to paper.
	/// </exception>
	/// <remarks>
	/// A paper mandate is confirmed by having nothing to confirm: it permits no real account, so nobody
	/// is asked anything and no variable is wanted.
	/// </remarks>
	public void Assert(TradingMandate mandate, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(mandate);

		if (!mandate.IsLive)
			return;

		if (mandate.Phrase.Length == 0)
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{mandate.Origin}' permits real money and carries no phrase, so there is " +
				"nothing anybody could type back to show they had read it. Nothing was started.");
		}

		if (_terminal.IsInteractive)
		{
			var typed = _terminal.Ask(Question(mandate), cancellationToken);

			// Nothing at all came back: the question was interrupted, or the input ended before a line
			// was typed. Kept apart from a wrong phrase because a person reading the journal afterwards
			// has to be able to tell "nobody answered" from "somebody answered badly", and an interrupt
			// at the terminal arrives as the first of those.
			if (typed is null)
			{
				throw new LiveMandateInvalidException(
					$"Nobody answered for the mandate at '{mandate.Origin}': the question was interrupted, or " +
					"the input ended before a line was typed. Nothing was started, and nothing was started on " +
					"a demo account instead - an unanswered question is not a smaller permission, it is none.");
			}

			if (Matches(typed, mandate.Phrase))
				return;

			throw new LiveMandateInvalidException(
				$"What was typed is not the phrase in the mandate at '{mandate.Origin}', so nothing was " +
				"started. It is compared exactly - the spacing and the capitalisation as they are written " +
				"there - because a phrase that matched approximately would be a phrase nobody had to read.");
		}

		if (_offered.Length == 0)
		{
			throw new LiveMandateInvalidException(
				$"The mandate at '{mandate.Origin}' permits real money and standard input here is not a " +
				$"terminal, so there is nobody to ask for its phrase. Set {PhraseVariable} to the phrase " +
				"written in that mandate, or start this runner at a terminal and type it. Nothing was started.");
		}

		if (!Matches(_offered, mandate.Phrase))
		{
			throw new LiveMandateInvalidException(
				$"{PhraseVariable} does not carry the phrase in the mandate at '{mandate.Origin}'. It is " +
				"compared exactly - the spacing and the capitalisation as they are written there - so nothing " +
				"was started.");
		}
	}

	/// <summary>
	/// Whether what came back is the phrase.
	/// </summary>
	/// <param name="answer">What the person typed, or what the variable carried.</param>
	/// <param name="phrase">What the mandate says.</param>
	/// <returns><see langword="true"/> when they are the same sequence of characters.</returns>
	/// <remarks>
	/// Ordinal, and nothing else. Not trimmed, so a phrase with a stray space is a different phrase and
	/// is refused as one; not case-folded; and not compared by any culture's rules, under which "ß" and
	/// "ss" are the same string and a Turkish machine disagrees with an English one about "I". A gate
	/// whose answer depends on the language the machine is set to is not a gate.
	///
	/// It is deliberately not a length-independent, constant-time comparison. That defence is for a
	/// secret an attacker gets to guess at repeatedly while measuring the answer, and this is not one:
	/// the phrase sits in a file on this machine, whoever can read that file already has it, and every
	/// attempt here costs a process start and leaves a refusal in the journal. Constant time would buy
	/// nothing and would say, to the next person reading this, that the phrase is a credential - which
	/// would invite somebody to use it as one instead of writing the sentence it is meant to be.
	/// </remarks>
	private static bool Matches(string answer, string phrase)
		=> string.Equals(answer, phrase, StringComparison.Ordinal);

	/// <summary>
	/// What a person at the terminal is shown before they are asked.
	/// </summary>
	/// <param name="mandate">The permission being exercised.</param>
	/// <returns>The question.</returns>
	/// <remarks>
	/// It says what is about to happen and never the phrase itself. Printing the phrase would turn typing
	/// it back into copying it off the screen, which is the one thing this whole arrangement is for.
	/// </remarks>
	private static string Question(TradingMandate mandate)
	{
		var line = Environment.NewLine;

		return line +
			$"  This runner is about to trade real money on account {mandate.Account}." + line +
			$"  {string.Join(", ", mandate.Symbols)}, at most " +
			$"{mandate.MaxPositionNotional.ToString(CultureInfo.InvariantCulture)} per position, until " +
			$"{mandate.ExpiresAt:O}." + line +
			$"  Authorised by the mandate at {mandate.Origin}." + line +
			line +
			"  Type the phrase written in that mandate to start it: ";
	}
}
