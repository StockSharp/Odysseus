namespace Odysseus.Products;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Builds the command line for one invocation of the installer console.
/// </summary>
/// <remarks>
/// Separate from the thing that starts the process, and pure, because this is where the vendor's
/// spelling lives and spelling is what a test can hold. The console is a Spectre.Console.Cli
/// application whose default command takes the verb as its first positional argument, then an optional
/// product identifier, then an optional directory - and positional arguments have to be contiguous, so
/// a directory cannot be given without a product.
///
/// The verbs are written exactly as the console's own enumeration spells them, which is the one
/// spelling that parses whether or not its converter happens to ignore case. The options are the long
/// forms of what its argument type declares.
/// </remarks>
public static class InstallerCommand
{
	/// <summary>Option that filters a listing by a phrase in the product name.</summary>
	public const string SearchOption = "--search";

	/// <summary>
	/// Option that makes an install idempotent by removing an installed product first. Its name says
	/// the opposite of what it does on this verb, which is the vendor's and not ours to rename.
	/// </summary>
	public const string ReinstallOption = "--noerror";

	/// <summary>Option that copies a product's settings aside before it is updated.</summary>
	public const string BackupOption = "--backup";

	/// <summary>Option that removes the settings, schemas and logs a product wrote along with it.</summary>
	public const string RemoveDataOption = "--data";

	/// <summary>
	/// Lists what the store offers.
	/// </summary>
	/// <param name="search">Phrase a product name must contain, or an empty string for all of them.</param>
	/// <returns>The arguments, in order.</returns>
	public static IReadOnlyList<string> Products(string search)
		=> Search(InstallerVerbs.Products, search);

	/// <summary>
	/// Lists what is installed on this machine.
	/// </summary>
	/// <param name="search">Phrase a product name must contain, or an empty string for all of them.</param>
	/// <returns>The arguments, in order.</returns>
	public static IReadOnlyList<string> Installed(string search)
		=> Search(InstallerVerbs.Installed, search);

	/// <summary>
	/// Installs one product into a directory.
	/// </summary>
	/// <param name="product">Identifier of the product.</param>
	/// <param name="directory">Where it is to land, which the caller of the tool never names.</param>
	/// <param name="reinstall">Whether an installed product is removed first rather than refused.</param>
	/// <returns>The arguments, in order.</returns>
	/// <exception cref="ArgumentOutOfRangeException">The identifier is not a positive number.</exception>
	/// <exception cref="ArgumentException">No directory was given.</exception>
	public static IReadOnlyList<string> Install(long product, string directory, bool reinstall)
	{
		Positive(product);
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		var arguments = new List<string> { Verb(InstallerVerbs.Install), Number(product), directory };

		if (reinstall)
			arguments.Add(ReinstallOption);

		return arguments;
	}

	/// <summary>
	/// Updates one installed product.
	/// </summary>
	/// <param name="product">Identifier of the product.</param>
	/// <param name="backupSettings">Whether its settings are copied aside first.</param>
	/// <returns>The arguments, in order.</returns>
	/// <exception cref="ArgumentOutOfRangeException">The identifier is not a positive number.</exception>
	/// <remarks>
	/// No directory: an update goes where the product already is, which the installer reads from its own
	/// registry of what it has installed.
	/// </remarks>
	public static IReadOnlyList<string> Update(long product, bool backupSettings)
	{
		Positive(product);

		var arguments = new List<string> { Verb(InstallerVerbs.Update), Number(product) };

		if (backupSettings)
			arguments.Add(BackupOption);

		return arguments;
	}

	/// <summary>
	/// Removes one installed product.
	/// </summary>
	/// <param name="product">Identifier of the product.</param>
	/// <param name="removeData">Whether what the product wrote goes with it.</param>
	/// <returns>The arguments, in order.</returns>
	/// <exception cref="ArgumentOutOfRangeException">The identifier is not a positive number.</exception>
	public static IReadOnlyList<string> Remove(long product, bool removeData)
	{
		Positive(product);

		var arguments = new List<string> { Verb(InstallerVerbs.Remove), Number(product) };

		if (removeData)
			arguments.Add(RemoveDataOption);

		return arguments;
	}

	/// <summary>
	/// Reads the identifier the store licences products against.
	/// </summary>
	/// <returns>The arguments, in order.</returns>
	/// <remarks>The one command that needs neither an account nor the network.</remarks>
	public static IReadOnlyList<string> HardwareId()
		=> [Verb(InstallerVerbs.HddId)];

	/// <summary>
	/// Spells a verb the way the console's own enumeration does.
	/// </summary>
	/// <param name="verb">Verb to spell.</param>
	/// <returns>The word to pass as the first argument.</returns>
	public static string Verb(InstallerVerbs verb)
		=> verb.ToString();

	private static IReadOnlyList<string> Search(InstallerVerbs verb, string search)
	{
		var arguments = new List<string> { Verb(verb) };

		// An empty phrase is not the same as no phrase to the console's filter, and passing the option
		// with nothing after it would leave the next argument to be read as its value.
		if (!string.IsNullOrWhiteSpace(search))
		{
			arguments.Add(SearchOption);
			arguments.Add(search.Trim());
		}

		return arguments;
	}

	private static string Number(long product)
		=> product.ToString(CultureInfo.InvariantCulture);

	private static void Positive(long product)
	{
		if (product <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(product), product, "A product is named by a positive numeric identifier.");
		}
	}
}
