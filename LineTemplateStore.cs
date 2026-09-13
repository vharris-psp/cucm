using System.Text.Json;

// Local, user-managed line templates (create/delete via 'vt cucm templates'), layered on top of
// any templates baked into the 'line-templates' configuration setting (config wins on name clash,
// since it's operator-controlled outside the running module).
internal sealed class LineTemplateStore(string dataDirectory)
{
    private const string Schema = "vt-cucm-line-templates/v1";
    private readonly string _path = ResolvePath(dataDirectory);
    private readonly string _lockPath = ResolvePath(dataDirectory) + ".lock";

    internal async Task<IReadOnlyDictionary<string, LineTemplate>> LoadAsync(CancellationToken ct = default)
    {
        await using var storeLock = await AcquireLockAsync(ct);
        return await LoadCoreAsync(ct);
    }

    internal async Task SaveAsync(string name, LineTemplate template, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var storeLock = await AcquireLockAsync(ct);
        var templates = (await LoadCoreAsync(ct)).ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        templates[name.Trim()] = template;
        await SaveCoreAsync(templates, ct);
    }

    internal async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var storeLock = await AcquireLockAsync(ct);
        var templates = (await LoadCoreAsync(ct)).ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!templates.Remove(name.Trim()))
        {
            return false;
        }
        await SaveCoreAsync(templates, ct);
        return true;
    }

    private async Task<IReadOnlyDictionary<string, LineTemplate>> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, LineTemplate>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schemaNode) ||
                schemaNode.GetString() != Schema ||
                !root.TryGetProperty("templates", out var templatesNode) ||
                templatesNode.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Line template store '{_path}' does not use the supported {Schema} schema.");
            }
            var templates = new Dictionary<string, LineTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in templatesNode.EnumerateObject())
            {
                var kind = ReadString(property.Value, "kind")?.ToLowerInvariant() switch
                {
                    "room" => LineTemplateKind.Room,
                    "user" => LineTemplateKind.User,
                    _ => throw new InvalidOperationException(
                        $"Line template store '{_path}' has template '{property.Name}' with an invalid kind."),
                };
                templates[property.Name] = new LineTemplate(
                    kind,
                    ReadString(property.Value, "routePartitionName"),
                    ReadString(property.Value, "alertingName"),
                    ReadString(property.Value, "display"),
                    ReadString(property.Value, "label"),
                    ReadString(property.Value, "externalPhoneNumberMask"),
                    property.Value.TryGetProperty("associateEndUser", out var associateNode) &&
                        associateNode.ValueKind == JsonValueKind.True,
                    ReadString(property.Value, "voiceMailProfileName"),
                    ReadString(property.Value, "pattern"));
            }
            return templates;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Line template store '{_path}' contains invalid JSON.",
                exception);
        }
    }

    private async Task SaveCoreAsync(IReadOnlyDictionary<string, LineTemplate> templates, CancellationToken ct)
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
                writer.WriteStartObject("templates");
                foreach (var (name, template) in templates.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject(name);
                    writer.WriteString("kind", template.Kind == LineTemplateKind.Room ? "room" : "user");
                    WriteOptional(writer, "routePartitionName", template.RoutePartitionName);
                    WriteOptional(writer, "alertingName", template.AlertingName);
                    WriteOptional(writer, "display", template.Display);
                    WriteOptional(writer, "label", template.Label);
                    WriteOptional(writer, "externalPhoneNumberMask", template.ExternalPhoneNumberMask);
                    WriteOptional(writer, "voiceMailProfileName", template.VoiceMailProfileName);
                    WriteOptional(writer, "pattern", template.Pattern);
                    writer.WriteBoolean("associateEndUser", template.AssociateEndUser);
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

    private static string ResolvePath(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException("VT did not provide the CUCM module data directory.");
        }
        return Path.Combine(Path.GetFullPath(dataDirectory), "line-templates.json");
    }

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
