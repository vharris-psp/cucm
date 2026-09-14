using System.Text.Json;

internal sealed class BuildingProfileStore(string dataDirectory)
{
    private const string Schema = "vt-cucm-building-profiles/v1";
    private readonly string _path = ResolvePath(dataDirectory);
    private readonly string _lockPath = ResolvePath(dataDirectory) + ".lock";

    internal bool Exists => File.Exists(_path);

    internal async Task<IReadOnlyDictionary<string, BuildingPattern>> LoadEffectiveAsync(
        string? legacyConfiguration,
        CancellationToken ct = default) =>
        Exists
            ? await LoadAsync(ct)
            : PhoneConfigurationChecks.ParseBuildingPatterns(legacyConfiguration ?? "{}");

    internal async Task<IReadOnlyDictionary<string, BuildingPattern>> LoadAsync(
        CancellationToken ct = default)
    {
        await using var storeLock = await AcquireLockAsync(ct);
        return await LoadCoreAsync(ct);
    }

    internal async Task SaveAsync(
        string code,
        BuildingPattern profile,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(profile);
        await using var storeLock = await AcquireLockAsync(ct);
        var profiles = (await LoadCoreAsync(ct)).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        profiles[code.Trim()] = Normalize(profile);
        Validate(profiles);
        await SaveCoreAsync(profiles, ct);
    }

    internal async Task ReplaceAsync(
        IReadOnlyDictionary<string, BuildingPattern> profiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        await using var storeLock = await AcquireLockAsync(ct);
        var normalized = profiles.ToDictionary(
            pair => pair.Key.Trim(),
            pair => Normalize(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        Validate(normalized);
        await SaveCoreAsync(normalized, ct);
    }

    internal async Task<bool> DeleteAsync(string code, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        await using var storeLock = await AcquireLockAsync(ct);
        var profiles = (await LoadCoreAsync(ct)).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        if (!profiles.Remove(code.Trim()))
        {
            return false;
        }
        await SaveCoreAsync(profiles, ct);
        return true;
    }

    private async Task<IReadOnlyDictionary<string, BuildingPattern>> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schemaNode) ||
                schemaNode.GetString() != Schema ||
                !root.TryGetProperty("buildings", out var buildingsNode) ||
                buildingsNode.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Building profile store '{_path}' does not use the supported {Schema} schema.");
            }

            var profiles = new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in buildingsNode.EnumerateObject())
            {
                var routePartitionName = ReadRequiredString(property.Value, "routePartitionName", property.Name);
                var devicePoolNames = ReadStringArray(property.Value, "devicePools");
                var devicePoolName = ReadString(property.Value, "devicePoolName");
                if (devicePoolName is not null &&
                    !devicePoolNames.Contains(devicePoolName, StringComparer.OrdinalIgnoreCase))
                {
                    devicePoolNames.Add(devicePoolName);
                }
                profiles[property.Name] = Normalize(new BuildingPattern(
                    routePartitionName,
                    devicePoolNames,
                    ReadString(property.Value, "phoneTemplateName"),
                    devicePoolName));
            }
            Validate(profiles);
            return profiles;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Building profile store '{_path}' contains invalid JSON.",
                exception);
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyDictionary<string, BuildingPattern> profiles,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
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
                await using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("schema", Schema);
                writer.WriteStartObject("buildings");
                foreach (var (code, profile) in profiles.OrderBy(
                    pair => pair.Key,
                    StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject(code);
                    writer.WriteString("routePartitionName", profile.RoutePartitionName);
                    WriteOptional(writer, "devicePoolName", profile.DevicePoolName);
                    writer.WriteStartArray("devicePools");
                    foreach (var devicePoolName in profile.DevicePoolNames)
                    {
                        writer.WriteStringValue(devicePoolName);
                    }
                    writer.WriteEndArray();
                    WriteOptional(writer, "phoneTemplateName", profile.PhoneTemplateName);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
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

    private static BuildingPattern Normalize(BuildingPattern profile)
    {
        var routePartitionName = NormalizeValue(profile.RoutePartitionName) ??
            throw new InvalidOperationException("A building profile requires a route partition.");
        var devicePoolName = NormalizeValue(profile.DevicePoolName);
        var devicePoolNames = profile.DevicePoolNames
            .Select(NormalizeValue)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (devicePoolName is not null &&
            !devicePoolNames.Contains(devicePoolName, StringComparer.OrdinalIgnoreCase))
        {
            devicePoolNames.Add(devicePoolName);
        }
        return new BuildingPattern(
            routePartitionName,
            devicePoolNames,
            NormalizeValue(profile.PhoneTemplateName),
            devicePoolName);
    }

    private static void Validate(IReadOnlyDictionary<string, BuildingPattern> profiles)
    {
        var assignedPools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("A building profile requires a non-empty code.");
            }
            foreach (var devicePoolName in profile.DevicePoolNames)
            {
                if (assignedPools.TryGetValue(devicePoolName, out var existingCode) &&
                    !existingCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Device pool '{devicePoolName}' is already assigned to building '{existingCode}'.");
                }
                assignedPools[devicePoolName] = code;
            }
        }
    }

    private static string ReadRequiredString(JsonElement item, string property, string code) =>
        ReadString(item, property) ?? throw new InvalidOperationException(
            $"Building profile '{code}' is missing '{property}'.");

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? NormalizeValue(node.GetString())
            : null;

    private static List<string> ReadStringArray(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var node))
        {
            return [];
        }
        if (node.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Building profile property '{property}' must be an array.");
        }
        return node.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? NormalizeValue(value.GetString())
                : null)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
    }

    private static string ResolvePath(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException("VT did not provide the CUCM module data directory.");
        }
        return Path.Combine(Path.GetFullPath(dataDirectory), "building-profiles.json");
    }

    private static string? NormalizeValue(string? value) =>
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