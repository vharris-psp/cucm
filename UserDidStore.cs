using System.Text.Json;

internal sealed record UserDid(
    string Pattern,
    string? RoutePartitionName,
    string? Description,
    string? CallingSearchSpaceName,
    string? VoiceMailProfileName,
    UserDidAssignment? Assignment = null);

internal sealed record UserDidAssignment(
    string PhoneName,
    int LineIndex,
    string? UserId,
    DateTimeOffset AssignedAt);

internal sealed class UserDidStore(string dataDirectory)
{
    private const string Schema = "vt-cucm-user-dids/v1";
    private readonly string _path = ResolvePath(dataDirectory);

    internal async Task<IReadOnlyList<UserDid>> LoadAsync(CancellationToken ct = default)
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
                !root.TryGetProperty("dids", out var didsNode) ||
                didsNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"User DID inventory '{_path}' does not use the supported {Schema} schema.");
            }

            var dids = new List<UserDid>();
            foreach (var item in didsNode.EnumerateArray())
            {
                var pattern = ReadString(item, "pattern");
                if (!IsPattern(pattern))
                {
                    throw new InvalidOperationException(
                        $"User DID inventory '{_path}' contains an invalid pattern.");
                }
                var assignment = ReadAssignment(item);
                var did = new UserDid(
                    pattern!,
                    ReadString(item, "routePartitionName"),
                    ReadString(item, "description"),
                    ReadString(item, "callingSearchSpaceName"),
                    ReadString(item, "voiceMailProfileName"),
                    assignment);
                if (dids.Any(existing => SameDid(existing, did)))
                {
                    throw new InvalidOperationException(
                        $"User DID inventory '{_path}' contains duplicate entries.");
                }
                dids.Add(did);
            }
            return dids
                .OrderBy(did => did.Pattern, StringComparer.Ordinal)
                .ThenBy(did => did.RoutePartitionName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"User DID inventory '{_path}' contains invalid JSON.",
                exception);
        }
    }

    internal async Task<int> AddAsync(
        IEnumerable<string> patterns,
        string? routePartitionName,
        string? descriptionPrefix,
        string? callingSearchSpaceName,
        string? voiceMailProfileName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var dids = (await LoadAsync(ct)).ToList();
        var added = 0;
        foreach (var pattern in patterns.Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (!IsPattern(pattern))
            {
                throw new InvalidOperationException($"'{pattern}' is not a valid DID pattern.");
            }
            var candidate = new UserDid(
                pattern,
                Normalize(routePartitionName),
                string.IsNullOrWhiteSpace(descriptionPrefix)
                    ? null
                    : $"{descriptionPrefix.Trim()} {pattern}",
                Normalize(callingSearchSpaceName),
                Normalize(voiceMailProfileName));
            if (dids.Any(existing => SameDid(existing, candidate)))
            {
                continue;
            }
            dids.Add(candidate);
            added++;
        }
        if (added > 0)
        {
            await SaveAsync(dids, ct);
        }
        return added;
    }

    internal async Task MarkAssignedAsync(
        string pattern,
        string? routePartitionName,
        string phoneName,
        int lineIndex,
        string? userId,
        CancellationToken ct = default)
    {
        var dids = (await LoadAsync(ct)).ToList();
        var target = dids.FindIndex(did =>
            did.Pattern.Equals(pattern, StringComparison.Ordinal) &&
            string.Equals(
                did.RoutePartitionName,
                Normalize(routePartitionName),
                StringComparison.Ordinal));
        if (target < 0)
        {
            throw new InvalidOperationException(
                $"User DID '{pattern}' is no longer present in the local inventory.");
        }
        if (dids[target].Assignment is not null)
        {
            throw new InvalidOperationException(
                $"User DID '{pattern}' is no longer available.");
        }

        for (var index = 0; index < dids.Count; index++)
        {
            if (dids[index].Assignment is { } assignment &&
                assignment.PhoneName.Equals(phoneName, StringComparison.OrdinalIgnoreCase) &&
                assignment.LineIndex == lineIndex)
            {
                dids[index] = dids[index] with { Assignment = null };
            }
        }
        dids[target] = dids[target] with
        {
            Assignment = new UserDidAssignment(
                phoneName,
                lineIndex,
                Normalize(userId),
                DateTimeOffset.UtcNow),
        };
        await SaveAsync(dids, ct);
    }

    internal static IReadOnlyList<string> ParsePatterns(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("At least one user DID is required.");
        }

        var patterns = new List<string>();
        foreach (var item in value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = item.Split("..", StringSplitOptions.TrimEntries);
            if (range.Length == 1)
            {
                patterns.Add(range[0]);
                continue;
            }
            if (range.Length != 2 ||
                !long.TryParse(range[0], out var start) ||
                !long.TryParse(range[1], out var end) ||
                start < 0 ||
                end < start ||
                range[0].Length != range[1].Length ||
                end - start > 10_000)
            {
                throw new InvalidOperationException(
                    $"Invalid DID range '{item}'. Use an equal-width numeric range such as 2313482000..2313482099 (maximum 10,001 values).");
            }
            for (var current = start; current <= end; current++)
            {
                patterns.Add(current.ToString($"D{range[0].Length}"));
            }
        }
        if (patterns.Count == 0 || patterns.Any(pattern => !IsPattern(pattern)))
        {
            throw new InvalidOperationException("The user DID list contains an invalid pattern.");
        }
        return patterns.Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task SaveAsync(IReadOnlyList<UserDid> dids, CancellationToken ct)
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
                writer.WriteStartArray("dids");
                foreach (var did in dids
                    .OrderBy(item => item.Pattern, StringComparer.Ordinal)
                    .ThenBy(item => item.RoutePartitionName, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("pattern", did.Pattern);
                    WriteOptional(writer, "routePartitionName", did.RoutePartitionName);
                    WriteOptional(writer, "description", did.Description);
                    WriteOptional(writer, "callingSearchSpaceName", did.CallingSearchSpaceName);
                    WriteOptional(writer, "voiceMailProfileName", did.VoiceMailProfileName);
                    if (did.Assignment is { } assignment)
                    {
                        writer.WriteStartObject("assignment");
                        writer.WriteString("phoneName", assignment.PhoneName);
                        writer.WriteNumber("lineIndex", assignment.LineIndex);
                        WriteOptional(writer, "userId", assignment.UserId);
                        writer.WriteString("assignedAt", assignment.AssignedAt);
                        writer.WriteEndObject();
                    }
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

    private static UserDidAssignment? ReadAssignment(JsonElement item)
    {
        if (!item.TryGetProperty("assignment", out var assignment) ||
            assignment.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var phoneName = ReadString(assignment, "phoneName");
        var lineIndex = assignment.TryGetProperty("lineIndex", out var lineIndexNode) &&
            lineIndexNode.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : 0;
        var assignedAt = assignment.TryGetProperty("assignedAt", out var assignedAtNode) &&
            assignedAtNode.TryGetDateTimeOffset(out var parsedAssignedAt)
                ? parsedAssignedAt
                : default;
        if (string.IsNullOrWhiteSpace(phoneName) || lineIndex <= 0 || assignedAt == default)
        {
            throw new InvalidOperationException("The user DID inventory contains an invalid assignment.");
        }
        return new UserDidAssignment(
            phoneName,
            lineIndex,
            ReadString(assignment, "userId"),
            assignedAt);
    }

    private static string ResolvePath(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException(
                "VT did not provide the CUCM module data directory.");
        }
        return Path.Combine(Path.GetFullPath(dataDirectory), "user-dids.json");
    }

    private static bool SameDid(UserDid left, UserDid right) =>
        left.Pattern.Equals(right.Pattern, StringComparison.Ordinal) &&
        string.Equals(left.RoutePartitionName, right.RoutePartitionName, StringComparison.Ordinal);

    private static bool IsPattern(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => !char.IsControl(character) &&
            !char.IsWhiteSpace(character) &&
            character is not ',' and not ';');

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? Normalize(node.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void WriteOptional(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(property, value);
        }
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}