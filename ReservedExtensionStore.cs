using System.Text.Json;

internal sealed record ReservedExtension(
    string Pattern,
    string RoutePartitionName,
    string? Note,
    DateTimeOffset RecordedAt);

/// <summary>
/// Numbers that live in a scanned CUCM DID partition but are known to be something other than a
/// user DID (e.g. a plain internal extension, a legacy number, etc.) — acknowledged here so
/// <see cref="UserDidReconciler"/> stops surfacing them as an untracked anomaly on every
/// 'dids list'. This never makes a number assignable; it only silences the noise.
/// </summary>
internal sealed class ReservedExtensionStore(string dataDirectory)
{
    private const string Schema = "vt-cucm-reserved-extensions/v1";
    private readonly string _path = ResolvePath(dataDirectory);
    private readonly string _lockPath = ResolvePath(dataDirectory) + ".lock";

    internal async Task<IReadOnlyList<ReservedExtension>> LoadAsync(CancellationToken ct = default)
    {
        await using var storeLock = await AcquireLockAsync(ct);
        return await LoadCoreAsync(ct);
    }

    private async Task<IReadOnlyList<ReservedExtension>> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schemaNode) ||
                schemaNode.GetString() != Schema ||
                !root.TryGetProperty("extensions", out var extensionsNode) ||
                extensionsNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"Reserved extension store '{_path}' does not use the supported {Schema} schema.");
            }

            var extensions = new List<ReservedExtension>();
            foreach (var item in extensionsNode.EnumerateArray())
            {
                var pattern = ReadString(item, "pattern");
                var partition = ReadString(item, "routePartitionName");
                if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(partition))
                {
                    throw new InvalidOperationException(
                        $"Reserved extension store '{_path}' contains an entry with a missing " +
                        "pattern or route partition.");
                }
                var recordedAt = item.TryGetProperty("recordedAt", out var recordedAtNode) &&
                    recordedAtNode.TryGetDateTimeOffset(out var parsedRecordedAt)
                        ? parsedRecordedAt
                        : default;
                extensions.Add(new ReservedExtension(pattern, partition, ReadString(item, "note"), recordedAt));
            }
            return extensions
                .OrderBy(extension => extension.Pattern, StringComparer.Ordinal)
                .ThenBy(extension => extension.RoutePartitionName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Reserved extension store '{_path}' contains invalid JSON.",
                exception);
        }
    }

    /// <summary>Adds any entries not already present (matched by pattern + partition); returns how many were added.</summary>
    internal async Task<int> AddAsync(
        IEnumerable<(string Pattern, string RoutePartitionName)> entries,
        string? note,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await using var storeLock = await AcquireLockAsync(ct);
        var extensions = (await LoadCoreAsync(ct)).ToList();
        var recordedAt = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var (pattern, routePartitionName) in entries)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(routePartitionName))
            {
                continue;
            }
            if (extensions.Any(existing => SameExtension(existing, pattern, routePartitionName)))
            {
                continue;
            }
            extensions.Add(new ReservedExtension(pattern.Trim(), routePartitionName.Trim(), note, recordedAt));
            added++;
        }
        if (added > 0)
        {
            await SaveCoreAsync(extensions, ct);
        }
        return added;
    }

    internal async Task<bool> RemoveAsync(
        string pattern,
        string routePartitionName,
        CancellationToken ct = default)
    {
        await using var storeLock = await AcquireLockAsync(ct);
        var extensions = (await LoadCoreAsync(ct)).ToList();
        var removed = extensions.RemoveAll(existing => SameExtension(existing, pattern, routePartitionName));
        if (removed > 0)
        {
            await SaveCoreAsync(extensions, ct);
        }
        return removed > 0;
    }

    private async Task SaveCoreAsync(IReadOnlyList<ReservedExtension> extensions, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("schema", Schema);
                writer.WriteStartArray("extensions");
                foreach (var extension in extensions
                    .OrderBy(item => item.Pattern, StringComparer.Ordinal)
                    .ThenBy(item => item.RoutePartitionName, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("pattern", extension.Pattern);
                    writer.WriteString("routePartitionName", extension.RoutePartitionName);
                    if (extension.Note is not null)
                    {
                        writer.WriteString("note", extension.Note);
                    }
                    writer.WriteString("recordedAt", extension.RecordedAt);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(ct);
            }
            SetOwnerOnly(temporary);
            File.Move(temporary, _path, overwrite: true);
            SetOwnerOnly(_path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
                SetOwnerOnly(_lockPath);
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private static bool SameExtension(ReservedExtension extension, string pattern, string routePartitionName) =>
        extension.Pattern.Equals(pattern, StringComparison.Ordinal) &&
        extension.RoutePartitionName.Equals(routePartitionName, StringComparison.Ordinal);

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? Normalize(node.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ResolvePath(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException(
                "VT did not provide the CUCM module data directory.");
        }
        return Path.Combine(Path.GetFullPath(dataDirectory), "reserved-extensions.json");
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
