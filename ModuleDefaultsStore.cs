using System.Text.Json;

internal enum ModuleDefaultsField
{
    Partition,
    CallingSearchSpace,
    VoiceMailProfile,
    DescriptionPrefix,
    ScanPartitions,
    ForwardCallingSearchSpace,
    CallingSearchSpaceActivationPolicy,
    NoAnswerRingDurationSeconds,
    ClassroomRoomLineTemplate,
    ClassroomUserLineTemplate,
}

// A single set of "vt cucm dids"/classroom defaults (partition, CSS, voicemail profile, forward
// CSS, activation policy, description prefix, scan partitions, no-answer ring duration, classroom
// room/user line template names), module-owned like BuildingProfileStore/LineTemplateStore.
// VTModuleSDK has no API for module code to write back to the YAML settings file, so once this
// local store exists it becomes authoritative; the legacy 'user-did-*'/'classroom-*-line-template'
// settings remain readable as a fallback/rollback source but are never auto-migrated - only
// 'vt cucm configure defaults' -> Save can populate this file.
internal sealed record ModuleDefaults(
    string? Partition = null,
    string? CallingSearchSpace = null,
    string? VoiceMailProfile = null,
    string? DescriptionPrefix = null,
    IReadOnlyList<string>? ScanPartitions = null,
    string? ForwardCallingSearchSpace = null,
    string? CallingSearchSpaceActivationPolicy = null,
    int? NoAnswerRingDurationSeconds = null,
    string? ClassroomRoomLineTemplate = null,
    string? ClassroomUserLineTemplate = null)
{
    public IReadOnlyList<string> ScanPartitions { get; init; } = ScanPartitions ?? [];

    // Parses only the 'user-did-*'/'classroom-*-line-template' settings directly, independent of
    // unrelated CucmConfiguration fields (port, publisher, axl-version) that have nothing to do
    // with these defaults and would otherwise force this store to fail on configuration
    // dictionaries that don't include them (e.g. in tests, or a future narrower context).
    internal static ModuleDefaults FromLegacyConfiguration(IReadOnlyDictionary<string, string> configuration)
    {
        string? Value(string key) =>
            configuration.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        return new ModuleDefaults(
            Value("user-did-partition"),
            Value("user-did-css"),
            Value("user-did-voicemail-profile"),
            Value("user-did-description-prefix"),
            UserDidReconciler.ParsePartitions(Value("user-did-scan-partitions")),
            Value("user-did-forward-css"),
            Value("user-did-css-activation-policy"),
            int.TryParse(Value("user-did-no-answer-ring-duration"), out var duration) ? duration : null,
            Value("classroom-room-line-template"),
            Value("classroom-user-line-template"));
    }
}

internal sealed class ModuleDefaultsStore(string dataDirectory)
{
    private const string Schema = "vt-cucm-module-defaults/v1";
    private readonly string _path = ResolvePath(dataDirectory);
    private readonly string _lockPath = ResolvePath(dataDirectory) + ".lock";

    internal bool Exists => File.Exists(_path);

    internal async Task<ModuleDefaults> LoadEffectiveAsync(
        IReadOnlyDictionary<string, string> configuration,
        CancellationToken ct = default) =>
        Exists
            ? await LoadAsync(ct)
            : ModuleDefaults.FromLegacyConfiguration(configuration);

    internal async Task<ModuleDefaults> LoadAsync(CancellationToken ct = default)
    {
        await using var storeLock = await AcquireLockAsync(ct);
        return await LoadCoreAsync(ct);
    }

    internal async Task SaveAsync(ModuleDefaults defaults, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        await using var storeLock = await AcquireLockAsync(ct);
        await SaveCoreAsync(Normalize(defaults), ct);
    }

    // Edits a single field while preserving every other field's current effective value (seeded
    // from the legacy configuration on the first edit, if this store doesn't exist yet) - callers
    // never need to resubmit the whole bundle just to change one setting.
    internal async Task<ModuleDefaults> SaveFieldAsync(
        ModuleDefaultsField field,
        string? rawValue,
        IReadOnlyDictionary<string, string> configuration,
        CancellationToken ct = default)
    {
        await using var storeLock = await AcquireLockAsync(ct);
        var current = File.Exists(_path)
            ? await LoadCoreAsync(ct)
            : ModuleDefaults.FromLegacyConfiguration(configuration);
        var updated = field switch
        {
            ModuleDefaultsField.Partition => current with { Partition = rawValue },
            ModuleDefaultsField.CallingSearchSpace => current with { CallingSearchSpace = rawValue },
            ModuleDefaultsField.VoiceMailProfile => current with { VoiceMailProfile = rawValue },
            ModuleDefaultsField.DescriptionPrefix => current with { DescriptionPrefix = rawValue },
            ModuleDefaultsField.ScanPartitions => current with
            {
                ScanPartitions = string.IsNullOrWhiteSpace(rawValue)
                    ? []
                    : rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            },
            ModuleDefaultsField.ForwardCallingSearchSpace => current with { ForwardCallingSearchSpace = rawValue },
            ModuleDefaultsField.CallingSearchSpaceActivationPolicy =>
                current with { CallingSearchSpaceActivationPolicy = rawValue },
            ModuleDefaultsField.NoAnswerRingDurationSeconds => current with
            {
                NoAnswerRingDurationSeconds = int.TryParse(rawValue, out var duration) ? duration : null,
            },
            ModuleDefaultsField.ClassroomRoomLineTemplate => current with { ClassroomRoomLineTemplate = rawValue },
            ModuleDefaultsField.ClassroomUserLineTemplate => current with { ClassroomUserLineTemplate = rawValue },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var normalized = Normalize(updated);
        await SaveCoreAsync(normalized, ct);
        return normalized;
    }

    private async Task<ModuleDefaults> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new ModuleDefaults();
        }
        await using var stream = File.OpenRead(_path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out var schemaNode) ||
            schemaNode.GetString() != Schema)
        {
            throw new InvalidOperationException(
                $"Module defaults store '{_path}' does not use the supported {Schema} schema.");
        }
        var scanPartitions = new List<string>();
        if (root.TryGetProperty("userDidScanPartitions", out var scanNode) &&
            scanNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in scanNode.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                {
                    scanPartitions.Add(entry.GetString()!.Trim());
                }
            }
        }
        return new ModuleDefaults(
            ReadString(root, "userDidPartition"),
            ReadString(root, "userDidCallingSearchSpace"),
            ReadString(root, "userDidVoiceMailProfile"),
            ReadString(root, "userDidDescriptionPrefix"),
            scanPartitions,
            ReadString(root, "userDidForwardCallingSearchSpace"),
            ReadString(root, "userDidCallingSearchSpaceActivationPolicy"),
            root.TryGetProperty("userDidNoAnswerRingDurationSeconds", out var durationNode) &&
                durationNode.ValueKind == JsonValueKind.Number
                ? durationNode.GetInt32()
                : null,
            ReadString(root, "classroomRoomLineTemplate"),
            ReadString(root, "classroomUserLineTemplate"));
    }

    private async Task SaveCoreAsync(ModuleDefaults defaults, CancellationToken ct)
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
                await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("schema", Schema);
                WriteOptional(writer, "userDidPartition", defaults.Partition);
                WriteOptional(writer, "userDidCallingSearchSpace", defaults.CallingSearchSpace);
                WriteOptional(writer, "userDidVoiceMailProfile", defaults.VoiceMailProfile);
                WriteOptional(writer, "userDidDescriptionPrefix", defaults.DescriptionPrefix);
                writer.WriteStartArray("userDidScanPartitions");
                foreach (var partition in defaults.ScanPartitions)
                {
                    writer.WriteStringValue(partition);
                }
                writer.WriteEndArray();
                WriteOptional(writer, "userDidForwardCallingSearchSpace", defaults.ForwardCallingSearchSpace);
                WriteOptional(
                    writer, "userDidCallingSearchSpaceActivationPolicy", defaults.CallingSearchSpaceActivationPolicy);
                if (defaults.NoAnswerRingDurationSeconds is { } duration)
                {
                    writer.WriteNumber("userDidNoAnswerRingDurationSeconds", duration);
                }
                WriteOptional(writer, "classroomRoomLineTemplate", defaults.ClassroomRoomLineTemplate);
                WriteOptional(writer, "classroomUserLineTemplate", defaults.ClassroomUserLineTemplate);
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
                    _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
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

    private static ModuleDefaults Normalize(ModuleDefaults defaults) =>
        defaults with
        {
            Partition = NormalizeValue(defaults.Partition),
            CallingSearchSpace = NormalizeValue(defaults.CallingSearchSpace),
            VoiceMailProfile = NormalizeValue(defaults.VoiceMailProfile),
            DescriptionPrefix = NormalizeValue(defaults.DescriptionPrefix),
            ScanPartitions = defaults.ScanPartitions
                .Select(NormalizeValue)
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ForwardCallingSearchSpace = NormalizeValue(defaults.ForwardCallingSearchSpace),
            CallingSearchSpaceActivationPolicy = NormalizeValue(defaults.CallingSearchSpaceActivationPolicy),
            ClassroomRoomLineTemplate = NormalizeValue(defaults.ClassroomRoomLineTemplate),
            ClassroomUserLineTemplate = NormalizeValue(defaults.ClassroomUserLineTemplate),
        };

    private static string ResolvePath(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException("VT did not provide the CUCM module data directory.");
        }
        return Path.Combine(Path.GetFullPath(dataDirectory), "module-defaults.json");
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? NormalizeValue(node.GetString())
            : null;

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
