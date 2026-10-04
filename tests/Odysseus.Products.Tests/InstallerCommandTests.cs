namespace Odysseus.Products.Tests;

using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Products;
using Odysseus.TestKit;

/// <summary>
/// How a command line for the installer console is built.
/// </summary>
/// <remarks>
/// The spellings here are the vendor's, read out of the argument type of their console rather than
/// remembered, and they are written out as literals so that a rename on their side is a failing test
/// rather than a silent agreement between this file and the one it is testing. Nothing else in this
/// product can catch a misspelt option: the console takes an unknown one as a parse failure that exits
/// through the same code as every other failure.
/// </remarks>
[TestClass]
public class InstallerCommandTests : OdysseusTestBase
{
	/// <summary>
	/// The verb is the first argument and is spelled as the console's own enumeration spells it, which
	/// is the one spelling that parses whether or not their converter ignores case.
	/// </summary>
	[TestMethod]
	public void TheVerbIsTheFirstArgument()
	{
		AreEqual("Products", InstallerCommand.Products(string.Empty)[0]);
		AreEqual("Installed", InstallerCommand.Installed(string.Empty)[0]);
		AreEqual("Install", InstallerCommand.Install(9, "/tmp/9", reinstall: false)[0]);
		AreEqual("Update", InstallerCommand.Update(9, backupSettings: false)[0]);
		AreEqual("Remove", InstallerCommand.Remove(9, removeData: false)[0]);
		AreEqual("HddId", InstallerCommand.HardwareId()[0]);
	}

	/// <summary>
	/// A search phrase that was not given is not passed as an empty one. The option would then take
	/// whatever came next as its value, and an empty phrase is not the same as no phrase to a filter.
	/// </summary>
	[TestMethod]
	public void APhraseThatWasNotGivenIsNotPassed()
	{
		AreEqual(1, InstallerCommand.Products(null).Count);
		AreEqual(1, InstallerCommand.Products(string.Empty).Count);
		AreEqual(1, InstallerCommand.Products("   ").Count, "a phrase of spaces was passed as a filter.");

		var searched = InstallerCommand.Products("  designer  ");

		AreEqual(3, searched.Count, Join(searched));
		AreEqual("--search", searched[1]);
		AreEqual("designer", searched[2], "the phrase was not trimmed before it was passed.");
	}

	/// <summary>
	/// The positional arguments have to be contiguous - a directory cannot be given without a product -
	/// so an install names the product and then the directory, in that order, before any option.
	/// </summary>
	[TestMethod]
	public void AnInstallNamesTheProductThenTheDirectory()
	{
		var arguments = InstallerCommand.Install(1137, "/data/products/1137", reinstall: false);

		AreEqual(3, arguments.Count, Join(arguments));
		AreEqual("Install", arguments[0]);
		AreEqual("1137", arguments[1]);
		AreEqual("/data/products/1137", arguments[2]);
	}

	/// <summary>
	/// Each of the three switches is the vendor's own long name. The one on install is named after
	/// suppressing errors and does something else entirely: on that verb it removes an installed product
	/// before putting it back, which is why this server calls it reinstall.
	/// </summary>
	[TestMethod]
	public void EachSwitchIsTheVendorsOwnName()
	{
		AreEqual("--noerror", InstallerCommand.Install(9, "/tmp/9", reinstall: true)[3]);
		AreEqual("--backup", InstallerCommand.Update(9, backupSettings: true)[2]);
		AreEqual("--data", InstallerCommand.Remove(9, removeData: true)[2]);
	}

	/// <summary>A switch that was not asked for is not passed at all.</summary>
	[TestMethod]
	public void ASwitchThatWasNotAskedForIsAbsent()
	{
		AreEqual(3, InstallerCommand.Install(9, "/tmp/9", reinstall: false).Count);
		AreEqual(2, InstallerCommand.Update(9, backupSettings: false).Count);
		AreEqual(2, InstallerCommand.Remove(9, removeData: false).Count);
	}

	/// <summary>
	/// An update goes where the product already is, which the installer reads from its own registry, so
	/// no directory is passed. Passing one would be this server deciding where somebody else's earlier
	/// installation lives.
	/// </summary>
	[TestMethod]
	public void AnUpdateNamesNoDirectory()
	{
		var arguments = InstallerCommand.Update(9, backupSettings: true);

		AreEqual(3, arguments.Count, Join(arguments));
		AreEqual("Update", arguments[0]);
		AreEqual("9", arguments[1]);
		AreEqual("--backup", arguments[2]);
	}

	/// <summary>
	/// An identifier is written the same way whatever the machine's culture is. The console parses it as
	/// a number, and a thousands separator or a different digit set is a parse failure that exits
	/// through the same code as everything else.
	/// </summary>
	[TestMethod]
	public void AnIdentifierIsWrittenTheSameWayEverywhere()
		=> AreEqual("1234567", InstallerCommand.Install(1234567, "/tmp/x", reinstall: false)[1]);

	/// <summary>
	/// A product identifier that is not a positive number names no product, and is refused here rather
	/// than passed on to be refused as a parse error nothing can read.
	/// </summary>
	[TestMethod]
	public void AnIdentifierThatNamesNoProductIsRefused()
	{
		Throws<ArgumentOutOfRangeException>(() => InstallerCommand.Install(0, "/tmp/x", reinstall: false));
		Throws<ArgumentOutOfRangeException>(() => InstallerCommand.Update(-1, backupSettings: false));
		Throws<ArgumentOutOfRangeException>(() => InstallerCommand.Remove(0, removeData: true));
	}

	/// <summary>An install with nowhere to go is refused rather than left to the console's own default.</summary>
	[TestMethod]
	public void AnInstallWithNoDirectoryIsRefused()
	{
		Throws<ArgumentNullException>(() => InstallerCommand.Install(9, null, reinstall: false));
		Throws<ArgumentException>(() => InstallerCommand.Install(9, "  ", reinstall: false));
	}

	private static string Join(IReadOnlyList<string> arguments)
		=> "the arguments were: " + string.Join(' ', arguments);
}
