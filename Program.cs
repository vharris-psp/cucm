using Vt.ModuleSdk;
using VSharp.Cucm;

return await ModuleApplication
    .Create("cucm", "CUCM")
    .Setting("publisher", "CUCM publisher hostname")
    .Setting("port", "CUCM AXL HTTPS port", defaultValue: "8443")
    .Setting("axl-version", "CUCM AXL schema version", defaultValue: "15.0")
    .Setting(
        "trusted-certificate",
        "Path to a PEM certificate trusted for CUCM AXL",
        required: false)
    .Secret("AXL_USERNAME", "CUCM AXL username")
    .Secret("AXL_PASSWORD", "CUCM AXL password")
    .Default("Run 'vt cucm users list' to list CUCM users.")
    .Command(
        "users",
        "Query CUCM users through AXL.",
        ListUsersAsync,
        ModuleResponseKind.Table)
    .RunAsync(args);

static async ValueTask<int> ListUsersAsync(ModuleContext context)
{
    if (!TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
    {
        context.Error.WriteLine("Usage: vt cucm users list [--max <count>] [--page-size <count>]");
        return 2;
    }

    try
    {
        var publisher = context.RequireConfiguration("publisher").Trim();
        if (!int.TryParse(context.RequireConfiguration("port"), out var port) ||
            port is < 1 or > 65535)
        {
            throw new InvalidOperationException("CUCM setting 'port' must be between 1 and 65535.");
        }
        var certificatePath = context.Configuration.GetValueOrDefault("trusted-certificate");
        var certificate = string.IsNullOrWhiteSpace(certificatePath)
            ? null
            : await File.ReadAllTextAsync(certificatePath, context.CancellationToken);
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, publisher, port, "axl/").Uri.ToString();
        using var cucm = new CucmService(new CucmServiceConfig(
            endpoint,
            context.RequireConfiguration("axl-version"),
            context.RequireSecret("AXL_USERNAME"),
            context.RequireSecret("AXL_PASSWORD"),
            certificate));

        var rows = new List<ModuleTableRow>();
        await foreach (var user in cucm.ListUsersAsync(
            maxRecords,
            pageSize,
            context.CancellationToken))
        {
            rows.Add(new ModuleTableRow(
                user.Uuid ?? user.UserId ?? $"user-{rows.Count + 1}",
                [
                    Clean(user.UserId),
                    Clean(user.DisplayName),
                    Clean(user.Email),
                    Clean(user.TelephoneNumber),
                    Clean(string.Join(",", user.AssociatedDevices)),
                ]));
        }
        await context.RespondAsync(new ModuleTableResponse(
            "CUCM users",
            ["USER ID", "DISPLAY NAME", "EMAIL", "PHONE", "DEVICES"],
            rows));
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or
                                               HttpRequestException or
                                               IOException or
                                               InvalidOperationException)
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static bool TryParseListArguments(
    IReadOnlyList<string> arguments,
    out int? maxRecords,
    out int pageSize)
{
    maxRecords = null;
    pageSize = CucmService.DefaultPageSize;
    if (arguments.Count == 0 || arguments[0] != "list")
    {
        return false;
    }
    for (var index = 1; index < arguments.Count; index++)
    {
        if (index + 1 >= arguments.Count ||
            !int.TryParse(arguments[index + 1], out var value) ||
            value <= 0)
        {
            return false;
        }
        switch (arguments[index])
        {
            case "--max":
                maxRecords = value;
                break;
            case "--page-size":
                pageSize = value;
                break;
            default:
                return false;
        }
        index++;
    }
    return true;
}

static string Clean(string? value) =>
    value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
