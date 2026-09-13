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
    string? RoutePartitionName,
    DateTimeOffset AssignedAt);

internal sealed class UserDidStore(string dataDirectory)
{
    private const string Schema = "vt-cucm-user-dids/v1";
    private readonly string _path = ResolvePath(dataDirectory);
    private readonly string _lockPath = ResolvePath(dataDirectory) + ".lock";

    internal async Task<IReadOnlyList<UserDid>> LoadAsync(CancellationToken ct = default)
    {
        await using var inventoryLock = await AcquireLockAsync(ct);
        return await LoadCoreAsync(ct);
    }

    private async Task<IReadOnlyList<UserDid>> LoadCoreAsync(CancellationToken ct)
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
        await using var inventoryLock = await AcquireLockAsync(ct);
        var dids = (await LoadCoreAsync(ct)).ToList();
        var added = 0;
        foreach (var pattern in patterns.Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (!IsUserExtension(pattern))
            {
                throw new InvalidOperationException(
                    $"'{pattern}' is not a valid four-digit user DN.");
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
            await SaveCoreAsync(dids, ct);
        }
        return added;
    }

    internal async Task ReplaceAsync(
        IEnumerable<string> patterns,
        string? routePartitionName,
        string? descriptionPrefix,
        string? callingSearchSpaceName,
        string? voiceMailProfileName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        await using var inventoryLock = await AcquireLockAsync(ct);
        var existing = await LoadCoreAsync(ct);
        if (existing.Any(did => did.Assignment is not null))
        {
            throw new InvalidOperationException(
                "The user DID inventory contains assignments and cannot be replaced.");
        }

        var replacement = patterns
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Select(pattern =>
            {
                if (!IsUserExtension(pattern))
                {
                    throw new InvalidOperationException(
                        $"'{pattern}' is not a valid four-digit user DN.");
                }
                return new UserDid(
                    pattern,
                    Normalize(routePartitionName),
                    string.IsNullOrWhiteSpace(descriptionPrefix)
                        ? null
                        : $"{descriptionPrefix.Trim()} {pattern}",
                    Normalize(callingSearchSpaceName),
                    Normalize(voiceMailProfileName));
            })
            .ToArray();
        await SaveCoreAsync(replacement, ct);
    }

    internal async Task MarkAssignedAsync(
        string pattern,
        string? routePartitionName,
        string phoneName,
        int lineIndex,
        string? userId,
        string? resolvedRoutePartitionName,
        CancellationToken ct = default)
    {
        await using var inventoryLock = await AcquireLockAsync(ct);
        var dids = (await LoadCoreAsync(ct)).ToList();
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
        if (dids[target].Assignment is { } existingAssignment &&
            !(existingAssignment.PhoneName.Equals(phoneName, StringComparison.OrdinalIgnoreCase) &&
              existingAssignment.LineIndex == lineIndex &&
              string.Equals(existingAssignment.UserId, Normalize(userId), StringComparison.OrdinalIgnoreCase)))
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
                Normalize(resolvedRoutePartitionName),
                DateTimeOffset.UtcNow),
        };
        await SaveCoreAsync(dids, ct);
    }

    internal async Task<bool> ClearAssignmentAsync(
        string phoneName,
        int lineIndex,
        CancellationToken ct = default)
    {
        await using var inventoryLock = await AcquireLockAsync(ct);
        var dids = (await LoadCoreAsync(ct)).ToList();
        var changed = false;
        for (var index = 0; index < dids.Count; index++)
        {
            if (dids[index].Assignment is { } assignment &&
                assignment.PhoneName.Equals(phoneName, StringComparison.OrdinalIgnoreCase) &&
                assignment.LineIndex == lineIndex)
            {
                dids[index] = dids[index] with { Assignment = null };
                changed = true;
            }
        }
        if (changed)
        {
            await SaveCoreAsync(dids, ct);
        }
        return changed;
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
        if (patterns.Count == 0 || patterns.Any(pattern => !IsUserExtension(pattern)))
        {
            throw new InvalidOperationException(
                "The user DN list must contain four-digit numeric extensions.");
        }
        return patterns.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string? NormalizeUserExtension(string? telephoneNumber)
    {
        if (string.IsNullOrWhiteSpace(telephoneNumber))
        {
            return null;
        }
        var digits = new string(telephoneNumber.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 4 ? digits : null;
    }

    private async Task SaveCoreAsync(IReadOnlyList<UserDid> dids, CancellationToken ct)
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
                        WriteOptional(
                            writer,
                            "routePartitionName",
                            assignment.RoutePartitionName);
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
            ReadString(assignment, "routePartitionName"),
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

    private static bool IsUserExtension(string? value) =>
        value is { Length: 4 } && value.All(char.IsAsciiDigit);

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