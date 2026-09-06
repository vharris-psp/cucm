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
    .Default("Run 'vt cucm users list' or 'vt cucm phones list'.")
    .Command(
        "users",
        "Browse CUCM users and their phones through AXL.",
        UsersAsync,
        ModuleResponseKind.Table)
    .Command(
        "phones",
        "Browse CUCM phones and update line appearance labels through AXL.",
        PhonesAsync,
        ModuleResponseKind.Table)
    .RunAsync(args);

static async ValueTask<int> UsersAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            var rows = new List<ModuleTableRow>();
            await foreach (var user in cucm.ListUsersAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var id = user.UserId ?? user.Uuid ?? $"user-{rows.Count + 1}";
                rows.Add(new ModuleTableRow(
                    id,
                    [
                        Clean(user.UserId),
                        Clean(user.DisplayName),
                        Clean(user.Email),
                        Clean(user.TelephoneNumber),
                        Clean(string.Join(",", user.AssociatedDevices)),
                    ],
                    string.IsNullOrWhiteSpace(user.UserId)
                        ? null
                        : ["users", "select", user.UserId]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM users",
                ["USER ID", "DISPLAY NAME", "EMAIL", "PHONE", "DEVICES"],
                rows));
            return 0;
        }
        if (context.Arguments is ["select", var userId])
        {
            var user = await cucm.GetUserAsync(userId, context.CancellationToken) ??
                throw new InvalidOperationException($"CUCM user '{userId}' was not found.");
            var rows = user.AssociatedDevices
                .Select(device => new ModuleTableRow(
                    device,
                    ["Phone", device],
                    ["phones", "select", device]))
                .Prepend(new ModuleTableRow(
                    "all-phones",
                    ["Browse", "All CUCM phones"],
                    ["phones", "list"]))
                .ToArray();
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM user: {user.UserId ?? userId}",
                ["ACTION", "DETAIL"],
                rows));
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm users list [--max <count>] [--page-size <count>]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async ValueTask<int> PhonesAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            var rows = new List<ModuleTableRow>();
            await foreach (var phone in cucm.ListPhonesAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var id = phone.Name ?? phone.Uuid ?? $"phone-{rows.Count + 1}";
                rows.Add(new ModuleTableRow(
                    id,
                    [
                        Clean(phone.Name),
                        Clean(phone.Description),
                        Clean(phone.Model ?? phone.Product),
                        Clean(phone.Protocol),
                        Clean(phone.OwnerUserName),
                    ],
                    string.IsNullOrWhiteSpace(phone.Name)
                        ? null
                        : ["phones", "select", phone.Name]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM phones",
                ["NAME", "DESCRIPTION", "MODEL", "PROTOCOL", "OWNER"],
                rows));
            return 0;
        }
        if (context.Arguments is ["select", var phoneName])
        {
            var phone = await RequirePhoneAsync(cucm, phoneName, context.CancellationToken);
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM phone: {phone.Name ?? phoneName}",
                ["ACTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "numbers",
                        ["Numbers", $"{phone.Lines.Count} line appearance(s)"],
                        ["phones", "lines", phoneName]),
                ]));
            return 0;
        }
        if (context.Arguments is ["lines", var phoneNameForLines])
        {
            var phone = await RequirePhoneAsync(
                cucm,
                phoneNameForLines,
                context.CancellationToken);
            var rows = phone.Lines.Select(line => new ModuleTableRow(
                $"{phoneNameForLines}:{line.Index}",
                [
                    line.Index.ToString(),
                    Clean(line.Pattern),
                    Clean(line.RoutePartitionName),
                    Clean(line.Label),
                    Clean(line.Display),
                ],
                ["phones", "line", phoneNameForLines, line.Index.ToString()])).ToArray();
            await context.RespondAsync(new ModuleTableResponse(
                $"Numbers on {phone.Name ?? phoneNameForLines}",
                ["INDEX", "NUMBER", "PARTITION", "LABEL", "DISPLAY"],
                rows));
            return 0;
        }
        if (context.Arguments is ["line", var phoneNameForLine, var indexText] &&
            int.TryParse(indexText, out var lineIndex) && lineIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForLine,
                lineIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTableResponse(
                $"{line.Pattern ?? "Number"} on {phoneNameForLine}",
                ["ACTION", "CURRENT VALUE"],
                [
                    new ModuleTableRow(
                        "set-dn",
                        ["Set directory number", Clean(line.Pattern)],
                        ["phones", "dn", phoneNameForLine, lineIndex.ToString()]),
                    new ModuleTableRow(
                        "set-label",
                        ["Set label", Clean(line.Label)],
                        ["phones", "label", phoneNameForLine, lineIndex.ToString()]),
                ]));
            return 0;
        }
        if (context.Arguments is ["dn", var phoneNameForDnPrompt, var dnIndexText] &&
            int.TryParse(dnIndexText, out var dnIndex) && dnIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForDnPrompt,
                dnIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set directory number for line {dnIndex} on {phoneNameForDnPrompt}",
                "Directory number",
                ["phones", "set-dn", phoneNameForDnPrompt, dnIndex.ToString()],
                line.Pattern));
            return 0;
        }
        if (context.Arguments is ["set-dn", var phoneNameForDnUpdate, var dnUpdateIndexText, var newDn] &&
            int.TryParse(dnUpdateIndexText, out var dnUpdateIndex) && dnUpdateIndex > 0)
        {
            await cucm.UpdatePhoneLineDirectoryNumberAsync(
                phoneNameForDnUpdate,
                dnUpdateIndex,
                newDn,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {dnUpdateIndex} on {phoneNameForDnUpdate} to directory number '{newDn}'.");
            return 0;
        }
        if (context.Arguments is ["label", var phoneNameForPrompt, var labelIndexText] &&
            int.TryParse(labelIndexText, out var labelIndex) && labelIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForPrompt,
                labelIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set label for {line.Pattern ?? $"line {labelIndex}"}",
                "Label",
                ["phones", "set-label", phoneNameForPrompt, labelIndex.ToString()],
                line.Label,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["set-label", var phoneNameToUpdate, var updateIndexText, var newLabel] &&
            int.TryParse(updateIndexText, out var updateIndex) && updateIndex > 0)
        {
            await cucm.UpdatePhoneLineLabelAsync(
                phoneNameToUpdate,
                updateIndex,
                newLabel,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {updateIndex} on {phoneNameToUpdate} with label '{newLabel}'.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm phones list [--max <count>] [--page-size <count>]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<CucmService> CreateCucmAsync(ModuleContext context)
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
    return new CucmService(new CucmServiceConfig(
        endpoint,
        context.RequireConfiguration("axl-version"),
        context.RequireSecret("AXL_USERNAME"),
        context.RequireSecret("AXL_PASSWORD"),
        certificate));
}

static async Task<VSharp.Cucm.Models.CucmPhone> RequirePhoneAsync(
    CucmService cucm,
    string phoneName,
    CancellationToken cancellationToken) =>
    await cucm.GetPhoneAsync(phoneName, cancellationToken) ??
        throw new InvalidOperationException($"CUCM phone '{phoneName}' was not found.");

static async Task<VSharp.Cucm.Models.CucmPhoneLineAppearance> RequireLineAsync(
    CucmService cucm,
    string phoneName,
    int lineIndex,
    CancellationToken cancellationToken)
{
    var phone = await RequirePhoneAsync(cucm, phoneName, cancellationToken);
    return phone.Lines.FirstOrDefault(line => line.Index == lineIndex) ??
        throw new InvalidOperationException(
            $"CUCM phone '{phoneName}' has no line appearance at index {lineIndex}.");
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

static bool IsExpected(Exception exception) =>
    exception is ArgumentException or
                 HttpRequestException or
                 IOException or
                 InvalidOperationException;

static string Clean(string? value) =>
    value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
