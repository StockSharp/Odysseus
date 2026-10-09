namespace StockSharp.Odysseus.Persistence;

using System.Text.Json.Serialization;

internal static class JsonFiles
{
	private static readonly JsonSerializerOptions _json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		IgnoreReadOnlyProperties = true,
		Converters = { new TypedIdJsonConverter(), new JsonStringEnumConverter(), new UtcDateTimeConverter() },
	};

	public static string PrepareRoot(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		root = Path.GetFullPath(root);
		Directory.CreateDirectory(root);

		var legacy = new[] { "operations", "closed-history" }
			.Any(name => File.Exists(Path.Combine(root, name + ".db")) && !File.Exists(Path.Combine(root, name + ".json")))
			|| Directory.EnumerateDirectories(root).Any(folder =>
				ProjectId.TryParse(Path.GetFileName(folder), out _) &&
				File.Exists(Path.Combine(folder, "odysseus.db")) && !File.Exists(Path.Combine(folder, "project.json")));

		if (legacy)
			throw new InvalidOperationException(
				$"This workspace contains SQLite data. Stop Odysseus and run python scripts/Migrate-SqliteWorkspace.py \"{root}\" before opening it. The original databases are kept unchanged.");

		return root;
	}

	public static async ValueTask<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
		where T : class
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!File.Exists(path))
			return null;

		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
			bufferSize: 4096, useAsync: true);

		return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken)
			?? throw new JsonException($"The file '{path}' contains no record.");
	}

	public static async ValueTask WriteAsync<T>(string path, T value, CancellationToken cancellationToken, bool overwrite = true)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		var temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";

		try
		{
			await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
				bufferSize: 4096, useAsync: true))
			{
				await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, path, overwrite);
		}
		finally
		{
			File.Delete(temporary);
		}
	}

	private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
	{
		public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
			=> reader.GetDateTime().ToUniversalTime();

		public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
			=> writer.WriteStringValue(value.ToUniversalTime());
	}
}
