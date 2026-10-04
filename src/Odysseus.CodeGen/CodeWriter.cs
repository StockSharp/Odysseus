namespace Odysseus.CodeGen;

using System;
using System.Text;

/// <summary>
/// Builds source text with fixed indentation and fixed line endings.
/// </summary>
/// <remarks>
/// Line endings are written explicitly rather than taken from the environment. Otherwise the same
/// specification would hash differently on Windows and on Linux, and a candidate would stop being the
/// same candidate depending on where it was translated.
/// </remarks>
internal sealed class CodeWriter
{
	private const string NewLine = "\n";

	private readonly StringBuilder _text = new();

	private int _depth;

	/// <summary>
	/// Writes a line at the current depth.
	/// </summary>
	/// <param name="text">Line to write.</param>
	public void Line(string text)
	{
		_text.Append('\t', _depth).Append(text).Append(NewLine);
	}

	/// <summary>
	/// Writes a line one level deeper, without opening a block.
	/// </summary>
	/// <param name="text">Line to write.</param>
	public void Indented(string text)
	{
		_text.Append('\t', _depth + 1).Append(text).Append(NewLine);
	}

	/// <summary>Writes an empty line.</summary>
	public void Blank()
	{
		_text.Append(NewLine);
	}

	/// <summary>Opens a block.</summary>
	public void Open()
	{
		Line("{");
		_depth++;
	}

	/// <summary>Closes a block.</summary>
	public void Close()
	{
		if (_depth == 0)
			throw new InvalidOperationException("There is no open block to close.");

		_depth--;
		Line("}");
	}

	/// <summary>
	/// Closes a block and appends text to the closing brace.
	/// </summary>
	/// <param name="suffix">Text to append, such as a semicolon.</param>
	public void CloseWith(string suffix)
	{
		if (_depth == 0)
			throw new InvalidOperationException("There is no open block to close.");

		_depth--;
		Line("}" + suffix);
	}

	/// <inheritdoc />
	public override string ToString()
		=> _text.ToString();
}
