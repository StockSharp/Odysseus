namespace StockSharp.Odysseus.Application;

using System.IO;
using System.Text.Json;

/// <summary>
/// The connector a server was started pointed at.
/// </summary>
/// <remarks>
/// A path rather than a set of environment variables, for the reason the credential file gives: a value
/// in the environment is inherited by every process the server starts. A small file also keeps the
/// settings of a connector - which are its own vocabulary and vary by venue - out of a start-up contract
/// this product would then have to know about.
///
/// The file is optional. Without it the server starts with no broker at all and says so, and a connector
/// can still be chosen while it runs.
/// </remarks>
public static class ConnectorFile
{
	/// <summary>
	/// Reads the connector a file names.
	/// </summary>
	/// <param name="path">Path of the file, or null when none was named.</param>
	/// <returns>The choice, or <see langword="null"/> when there is no usable file.</returns>
	/// <exception cref="ConnectorRefusedException">The file exists and cannot be read as a connector.</exception>
	public static ConnectorChoice Read(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return null;

		return Parse(File.ReadAllText(path), path);
	}

	/// <summary>
	/// Reads the connector out of the text of such a file.
	/// </summary>
	/// <param name="text">Contents of the file.</param>
	/// <param name="origin">Where the text came from, for the message when it cannot be read.</param>
	/// <returns>The choice.</returns>
	/// <exception cref="ConnectorRefusedException">The text is not a connector.</exception>
	public static ConnectorChoice Parse(string text, string origin)
	{
		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(text);
		}
		catch (JsonException error)
		{
			throw new ConnectorRefusedException(
				$"{origin} is not a connector file: {error.Message}. It holds an object with 'packageId' and, " +
				"optionally, 'version', 'adapter' and 'settings'.");
		}

		using (document)
		{
			var root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object)
				throw new ConnectorRefusedException($"{origin} holds {root.ValueKind} where a connector object was expected.");

			var packageId = Text(root, "packageId");

			if (packageId.Length == 0)
				throw new ConnectorRefusedException($"{origin} names no 'packageId', so there is no connector to load.");

			return new(packageId, Text(root, "version"), Text(root, "adapter"), Settings(root, origin));
		}
	}

	/// <summary>
	/// Reads a settings object written on its own, as a tool argument rather than as a file.
	/// </summary>
	/// <param name="json">A JSON object of setting names and values, or empty for none.</param>
	/// <param name="origin">Where the text came from, for the message when it cannot be read.</param>
	/// <returns>The settings.</returns>
	/// <exception cref="ConnectorRefusedException">The text is not an object of settings.</exception>
	public static IReadOnlyDictionary<string, string> ReadSettings(string json, string origin)
	{
		if (string.IsNullOrWhiteSpace(json))
			return new Dictionary<string, string>(StringComparer.Ordinal);

		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(json);
		}
		catch (JsonException error)
		{
			throw new ConnectorRefusedException(
				$"{origin} is not a JSON object of settings: {error.Message}. Write it as " +
				"{\"SettingName\": \"value\"}.");
		}

		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				throw new ConnectorRefusedException($"{origin} holds {document.RootElement.ValueKind} where an object of settings was expected.");

			return Read(document.RootElement, origin);
		}
	}

	private static IReadOnlyDictionary<string, string> Settings(JsonElement root, string origin)
	{
		if (!root.TryGetProperty("settings", out var declared))
			return new Dictionary<string, string>(StringComparer.Ordinal);

		if (declared.ValueKind != JsonValueKind.Object)
			throw new ConnectorRefusedException($"{origin} holds 'settings' as {declared.ValueKind} where an object was expected.");

		return Read(declared, origin);
	}

	private static IReadOnlyDictionary<string, string> Read(JsonElement declared, string origin)
	{
		var settings = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var setting in declared.EnumerateObject())
		{
			if (setting.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
			{
				throw new ConnectorRefusedException(
					$"{origin} holds '{setting.Name}' as {setting.Value.ValueKind}. A setting is a single value; a " +
					"connector that takes a list takes it as text separated by commas.");
			}

			// Written as whatever reads naturally in a configuration file and converted by the type the
			// adapter declares, so a caller never has to know that a setting happens to be a number.
			settings[setting.Name] = setting.Value.ValueKind == JsonValueKind.String
				? setting.Value.GetString()
				: setting.Value.GetRawText();
		}

		return settings;
	}

	private static string Text(JsonElement root, string name)
		=> root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: string.Empty;
}
