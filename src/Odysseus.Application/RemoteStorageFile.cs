namespace Odysseus.Application;

using System.IO;
using System.Text.Json;

/// <summary>
/// The remote storage server a server was started pointed at.
/// </summary>
/// <remarks>
/// A file rather than environment variables for the reason the credential file gives: a value in the
/// environment is inherited by every process the server starts, and this one may hold a password.
/// </remarks>
public static class RemoteStorageFile
{
	/// <summary>
	/// Reads the server a file names.
	/// </summary>
	/// <param name="path">Path of the file, or null when none was named.</param>
	/// <returns>The choice, or <see langword="null"/> when there is no usable file.</returns>
	/// <exception cref="ConnectorRefusedException">The file exists and cannot be read as a storage server.</exception>
	public static RemoteStorageChoice Read(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return null;

		return Parse(File.ReadAllText(path), path);
	}

	/// <summary>
	/// Reads the server out of the text of such a file.
	/// </summary>
	/// <param name="text">Contents of the file.</param>
	/// <param name="origin">Where the text came from, for the message when it cannot be read.</param>
	/// <returns>The choice.</returns>
	/// <exception cref="ConnectorRefusedException">The text is not a storage server.</exception>
	public static RemoteStorageChoice Parse(string text, string origin)
	{
		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(text);
		}
		catch (JsonException error)
		{
			throw new ConnectorRefusedException(
				$"{origin} is not a storage file: {error.Message}. It holds an object with 'address' and, " +
				"optionally, 'login' and 'password'.");
		}

		using (document)
		{
			var root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object)
				throw new ConnectorRefusedException($"{origin} holds {root.ValueKind} where a storage object was expected.");

			var address = Text(root, "address");

			if (address.Length == 0)
				throw new ConnectorRefusedException($"{origin} names no 'address', so there is no server to read from.");

			return new(address, Text(root, "login"), Text(root, "password"));
		}
	}

	private static string Text(JsonElement root, string name)
		=> root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString().Trim()
			: string.Empty;
}
