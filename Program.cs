using Vt.ModuleSdk;
using VSharp.Cucm;
using VSharp.Cucm.Models;
using VSharp.Patterns.Strings;
using System.Reflection;

return await ModuleApplication
    .Create("cucm", "CUCM")
    .Setting("publisher", "CUCM publisher hostname")
    .Setting("port", "CUCM AXL HTTPS port", defaultValue: "8443")
    .Setting("axl-version", "CUCM AXL schema version", defaultValue: "15.0")
    .Setting(
        "trusted-certificate",
        "Path to a PEM certificate trusted for CUCM AXL",
        required: false)
    .Setting(
        "room-partitions",
        "JSON object mapping phone locations to CUCM room route partitions",
        required: false,
        defaultValue: "{}")
    .Setting(
        "building-patterns",
        "JSON object mapping building codes to { routePartitionName, devicePools[], phoneTemplateName? } " +
            "used for room DN creation, classroom phone-template application, and room-routing checks",
        required: false,
        defaultValue: "{}")
    .Setting(
        "template-compliance-policies",
        "JSON object mapping phone button template names to { slots: [{ index, kind: \"user\"|" +
            "\"room\" }] }, used to auto-compose each phone's Description as a compliance summary. " +
            "Templates with no entry here are always treated as unevaluable ('?').",
        required: false,
        defaultValue: "{}")
    .Setting(
        "line-templates",
        "JSON object mapping line template names to { kind: \"room\"|\"user\", routePartitionName, " +
            "alertingName, display, label, externalPhoneNumberMask, associateEndUser }, applied to a " +
            "phone's line in one step via 'Set DN options' -> Apply Template (F1). String fields may " +
            "use tokens: {room} {building} {pattern} {phoneName} {lineIndex} {devicePoolName} " +
            "{userDisplayName} {userId}. 'room' kind ignores routePartitionName (derived from the " +
            "phone's button template prefix against 'building-patterns' instead).",
        required: false,
        defaultValue: "{}")
    .Setting(
        "phone-check-placeholder",
        "Temporary JSON assignment with userDn, roomNumber, and location",
        required: false,
        defaultValue: "{\"userDn\":\"1000\",\"roomNumber\":\"3000\",\"location\":\"default\"}")
    .Setting(
        "user-did-partition",
        "Route partition used when importing and creating user DIDs",
        required: false)
    .Setting(
        "user-did-css",
        "Calling search space used when creating user DIDs",
        required: false)
    .Setting(
        "user-did-voicemail-profile",
        "Voicemail profile used when creating user DIDs",
        required: false)
    .Setting(
        "user-did-description-prefix",
        "Description prefix used when creating user DIDs",
        required: false,
        defaultValue: "User DID")
    .Secret("AXL_USERNAME", "CUCM AXL username")
    .Secret("AXL_PASSWORD", "CUCM AXL password")
    .Default(
        RootAsync,
        "Show the CUCM home menu: users, phones, directory numbers, the local DID inventory, " +
            "phone provisioning, and data export.",
        ModuleResponseKind.Table)
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
    .Command(
        "dn",
        "List, inspect, and create CUCM directory numbers through AXL.",
        DirectoryNumbersAsync,
        ModuleResponseKind.Table)
    .Command(
        "dids",
        "Manage the local approved inventory of user DNs.",
        UserDidsAsync,
        ModuleResponseKind.Table)
    .Command(
        "number",
        "Alias for the CUCM directory-number command.",
        DirectoryNumbersAsync,
        ModuleResponseKind.Table)
    .Command(
        "provision",
        "Provision a CUCM phone from scratch for a user (create/claim, assign DN, review, save).",
        ProvisionAsync,
        ModuleResponseKind.Table)
    .Command(
        "get",
        "Query and export CUCM data (e.g. phones by device pool) to the console or a file.",
        GetAsync,
        ModuleResponseKind.Table)
    .Command(
        "templates",
        "Create, review, and delete local line templates used by 'Apply template' and the classroom flow.",
        TemplatesAsync,
        ModuleResponseKind.Table)
    .RunAsync(args);

static async ValueTask<int> RootAsync(ModuleContext context)
{
    var publisher = context.Configuration.GetValueOrDefault("publisher");
    await context.RespondAsync(new ModuleTableResponse(
        string.IsNullOrWhiteSpace(publisher) ? "CUCM" : $"CUCM — {publisher}",
        ["AREA", "DESCRIPTION"],
        [
            new ModuleTableRow(
                "provision",
                ["Provision a phone", "Create or claim a phone, assign a user and DN, from scratch"],
                ["provision"]),
            new ModuleTableRow(
                "phones",
                ["Phones", "Browse CUCM phones, lines/DNs, room DNs, checks, and edits"],
                ["phones", "list"]),
            new ModuleTableRow(
                "users",
                ["Users", "Browse CUCM users and assign phones and DNs"],
                ["users", "list"]),
            new ModuleTableRow(
                "dn",
                ["Directory numbers", "List, inspect, and create CUCM directory numbers"],
                ["dn", "list"]),
            new ModuleTableRow(
                "dids",
                ["User DID inventory", "Manage the local approved inventory of user DNs"],
                ["dids"]),
            new ModuleTableRow(
                "get",
                ["Export CUCM data", "Query phones by device pool and export to console or a file"],
                ["get"]),
            new ModuleTableRow(
                "templates",
                ["Line templates", "Create and delete local line templates"],
                ["templates"]),
        ]));
    return 0;
}

// Composite wizard that chains the existing phone (add/edit) and user (assign) primitives into
// a single guided flow: pick "new" or "existing" phone, select a user with an available local
// DID, review, then create/claim the phone + assign the DID/line to that user in one submit.
static async ValueTask<int> ProvisionAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (context.Arguments.Count == 0)
        {
            await context.RespondAsync(new ModuleTableResponse(
                "Provision a phone",
                ["OPTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "new",
                        ["New phone", "Create a new CUCM phone from scratch"],
                        ["provision", "new"]),
                    new ModuleTableRow(
                        "existing",
                        ["Existing phone", "Claim an unowned CUCM phone"],
                        ["provision", "existing"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["new"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Provision a new phone",
                "Phone name",
                ["provision", "new-description"]));
            return 0;
        }
        if (context.Arguments is ["new-description", var newName] &&
            !string.IsNullOrWhiteSpace(newName))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Provision {newName}",
                "Description",
                ["provision", "new-product", newName],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["new-product", var productName, var productDescription])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Provision {productName}",
                "Product (e.g. \"Cisco 8841\")",
                ["provision", "new-device-pool", productName, productDescription]));
            return 0;
        }
        if (context.Arguments is
            ["new-device-pool", var dpName, var dpDescription, var dpProduct] &&
            !string.IsNullOrWhiteSpace(dpProduct))
        {
            await RespondWithProvisionNamedSelectorAsync(
                context,
                cucm,
                "Select device pool",
                "new-template",
                new ProvisionWizardState(true, dpName, Normalize(dpDescription), dpProduct),
                "device-pool",
                allowNone: false);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "new-template", out var templateState))
        {
            await RespondWithProvisionNamedSelectorAsync(
                context,
                cucm,
                "Select phone button template",
                "new-security-profile",
                templateState,
                "phone-template",
                allowNone: true);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "new-security-profile", out var securityState))
        {
            await RespondWithProvisionNamedSelectorAsync(
                context,
                cucm,
                "Select security profile",
                "new-user",
                securityState,
                "security-profile",
                allowNone: true);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "new-user", out var userSelectionState))
        {
            await RespondWithProvisionUserSelectorAsync(context, cucm, userSelectionState);
            return 0;
        }
        if (context.Arguments is ["existing"])
        {
            var rows = new List<ModuleTableRow>();
            await foreach (var phone in cucm.ListPhonesAsync(cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(phone.Name))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    phone.Name,
                    [
                        Clean(phone.Name), Clean(phone.Description), Clean(phone.Model ?? phone.Product),
                        Clean(phone.DevicePoolName), Clean(phone.OwnerUserName),
                    ],
                    ["provision", "existing-selected", phone.Name]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM phones",
                ["NAME", "DESCRIPTION", "MODEL", "DEVICE POOL", "OWNER"],
                rows));
            return 0;
        }
        if (context.Arguments is ["existing-selected", var existingPhoneName])
        {
            var phone = await RequirePhoneAsync(cucm, existingPhoneName, context.CancellationToken);
            var existingState = new ProvisionWizardState(
                false,
                phone.Name ?? existingPhoneName,
                PhoneConfigurationChecks.ExtractRawDescription(phone.Description),
                phone.Product,
                phone.DevicePoolName,
                phone.PhoneTemplateName,
                phone.SecurityProfileName,
                PreviousOwnerUserName: Normalize(phone.OwnerUserName));
            await RespondWithProvisionUserSelectorAsync(context, cucm, existingState);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "review", out var reviewState) &&
            !string.IsNullOrWhiteSpace(reviewState.UserId))
        {
            var reviewUser = await RequireUserAsync(cucm, reviewState.UserId, context.CancellationToken);
            var reviewDid = await RequireAvailableUserDidForUserAsync(context, reviewUser);
            await context.RespondAsync(new ModuleTableResponse(
                "Review phone provisioning",
                ["PHONE", "MODE", "PRODUCT", "DEVICE POOL", "USER", "USER DN", "OWNER ACTION"],
                [
                    new ModuleTableRow(
                        "submit",
                        [
                            reviewState.PhoneName,
                            reviewState.IsNewPhone ? "Create new" : "Use existing",
                            Clean(reviewState.Product),
                            Clean(reviewState.DevicePoolName),
                            reviewUser.DisplayName ?? reviewState.UserId,
                            reviewDid.Pattern,
                            OwnerActionLabel(reviewState.PreviousOwnerUserName, reviewState.UserId),
                        ],
                        [
                            "provision", "create", reviewState.IsNewPhone.ToString(), reviewState.PhoneName,
                            reviewState.Description ?? string.Empty, reviewState.Product ?? string.Empty,
                            reviewState.DevicePoolName ?? string.Empty, reviewState.PhoneTemplateName ?? string.Empty,
                            reviewState.SecurityProfileName ?? string.Empty, reviewState.UserId,
                            reviewState.PreviousOwnerUserName ?? string.Empty,
                            reviewDid.Pattern, reviewDid.RoutePartitionName ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments.Count == 12 && context.Arguments[0] == "create" &&
            bool.TryParse(context.Arguments[1], out var createIsNew))
        {
            var createState = new ProvisionWizardState(
                createIsNew,
                context.Arguments[2],
                Normalize(context.Arguments[3]),
                Normalize(context.Arguments[4]),
                Normalize(context.Arguments[5]),
                Normalize(context.Arguments[6]),
                Normalize(context.Arguments[7]),
                Normalize(context.Arguments[8]),
                Normalize(context.Arguments[9]));
            var createPattern = context.Arguments[10];
            var createPartition = context.Arguments[11];
            var createUserId = createState.UserId ??
                throw new InvalidOperationException("Provisioning state is missing a user ID.");

            var user = await RequireUserAsync(cucm, createUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidAsync(context, createPattern, createPartition);
            var previousOwnerUserId = createState.PreviousOwnerUserName;
            var replacingOwner = previousOwnerUserId is not null &&
                !previousOwnerUserId.Equals(createUserId, StringComparison.OrdinalIgnoreCase);

            string? phoneUuid = null;
            if (createState.IsNewPhone)
            {
                phoneUuid = await cucm.AddPhoneAsync(
                    new CucmPhoneCreateRequest(
                        createState.PhoneName,
                        createState.Product ?? string.Empty,
                        createState.DevicePoolName ?? string.Empty,
                        createState.Description,
                        PhoneTemplateName: createState.PhoneTemplateName,
                        SecurityProfileName: createState.SecurityProfileName,
                        OwnerUserName: createUserId),
                    context.CancellationToken);
            }
            else
            {
                await cucm.UpdatePhoneAsync(
                    createState.PhoneName,
                    null,
                    null,
                    createUserId,
                    context.CancellationToken);
            }

            var previousDevices = user.AssociatedDevices.ToArray();
            var addAssociation = !previousDevices.Contains(
                createState.PhoneName,
                StringComparer.OrdinalIgnoreCase);
            if (addAssociation)
            {
                await cucm.UpdateUserAssociatedDevicesAsync(
                    createUserId,
                    previousDevices.Append(createState.PhoneName),
                    context.CancellationToken);
            }

            var effectivePartition = Normalize(createPartition);
            var existingDn = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                effectivePartition,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existingDn);
            if (existingDn is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        did.Pattern,
                        effectivePartition,
                        did.Description,
                        did.CallingSearchSpaceName,
                        did.VoiceMailProfileName),
                    context.CancellationToken);
            }
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberToUserAsync(
                    createState.PhoneName,
                    1,
                    did.Pattern,
                    effectivePartition,
                    createUserId,
                    context.CancellationToken);
            }
            catch (Exception assignmentException)
            {
                if (addAssociation)
                {
                    try
                    {
                        await cucm.UpdateUserAssociatedDevicesAsync(
                            createUserId,
                            previousDevices,
                            context.CancellationToken);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            $"Phone '{createState.PhoneName}' was {(createState.IsNewPhone ? "created" : "updated")} " +
                            $"and owned by '{createUserId}', but line 1 could not be assigned, and the " +
                            "user-device association could not be rolled back.",
                            new AggregateException(assignmentException, rollbackException));
                    }
                }
                throw new InvalidOperationException(
                    $"Phone '{createState.PhoneName}' was {(createState.IsNewPhone ? "created" : "updated")} and " +
                    $"owned by '{createUserId}', but line 1 could not be assigned: {assignmentException.Message} " +
                    $"Use 'phones select {createState.PhoneName}' to finish the line assignment.",
                    assignmentException);
            }
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern,
                did.RoutePartitionName,
                createState.PhoneName,
                1,
                createUserId,
                effectivePartition,
                context.CancellationToken);
            var createWarnings = new List<string>();
            if (replacingOwner)
            {
                try
                {
                    await RemovePhoneFromPreviousOwnerAsync(
                        cucm,
                        previousOwnerUserId!,
                        createState.PhoneName,
                        context.CancellationToken);
                }
                catch (Exception cleanupException)
                {
                    createWarnings.Add(
                        $"could not remove '{createState.PhoneName}' from previous owner " +
                        $"'{previousOwnerUserId}' associated devices: {cleanupException.Message}");
                }
            }
            try
            {
                await EnsureRoomLineAsync(cucm, createState.PhoneName, context.CancellationToken);
            }
            catch (Exception roomException)
            {
                createWarnings.Add($"could not ensure a room DN on line 3: {roomException.Message}");
            }
            var composedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, createState.PhoneName, createState.Description, context.CancellationToken);
            context.Output.WriteLine(
                $"Provisioned phone '{createState.PhoneName}' for user '{createUserId}' with DN " +
                $"'{did.Pattern}'" + (string.IsNullOrWhiteSpace(phoneUuid) ? "." : $" ({phoneUuid}).") +
                (replacingOwner ? $" Replaced previous owner '{previousOwnerUserId}'." : string.Empty) +
                $" Description: '{composedDescription}'." +
                (createWarnings.Count == 0
                    ? string.Empty
                    : " WARNING: " + string.Join(" ", createWarnings)));
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm provision\n" +
            "       vt cucm provision new\n" +
            "       vt cucm provision existing");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task RespondWithProvisionNamedSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    string title,
    string nextRoute,
    ProvisionWizardState state,
    string resourceType,
    bool allowNone)
{
    var rows = new List<ModuleTableRow>();
    if (allowNone)
    {
        rows.Add(new ModuleTableRow(
            $"none:{resourceType}",
            ["<None>", string.Empty],
            ProvisionWizardArguments(nextRoute, state)));
    }
    var resources = resourceType switch
    {
        "device-pool" => cucm.ListDevicePoolsAsync(cancellationToken: context.CancellationToken),
        "phone-template" => cucm.ListPhoneButtonTemplatesAsync(
            cancellationToken: context.CancellationToken),
        "security-profile" => cucm.ListPhoneSecurityProfilesAsync(
            cancellationToken: context.CancellationToken),
        _ => throw new InvalidOperationException(
            $"Unknown CUCM phone resource type '{resourceType}'."),
    };
    await foreach (var resource in resources)
    {
        if (string.IsNullOrWhiteSpace(resource.Name))
        {
            continue;
        }
        rows.Add(new ModuleTableRow(
            resource.Uuid ?? $"{resourceType}:{resource.Name}",
            [Clean(resource.Name), Clean(resource.Description)],
            ProvisionWizardArguments(nextRoute, SetProvisionWizardResource(state, resourceType, resource.Name))));
    }
    await context.RespondAsync(new ModuleTableResponse(
        title,
        ["NAME", "DESCRIPTION"],
        rows,
        SubmitMode: ModuleTableSubmitMode.Save));
}

static async Task RespondWithProvisionUserSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    ProvisionWizardState state)
{
    var dids = await new UserDidStore(context.DataDirectory).LoadAsync(context.CancellationToken);
    var rows = new List<ModuleTableRow>();
    await foreach (var user in cucm.ListUsersAsync(cancellationToken: context.CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(user.UserId))
        {
            continue;
        }
        var extension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
        var status = UserDnStatus(user.UserId, extension, dids);
        rows.Add(new ModuleTableRow(
            user.UserId,
            [Clean(user.UserId), Clean(user.DisplayName), Clean(extension), status],
            status == "Available"
                ? ProvisionWizardArguments("review", state with { UserId = user.UserId })
                : null));
    }
    await context.RespondAsync(new ModuleTableResponse(
        $"Select a user for {state.PhoneName}",
        ["USER ID", "DISPLAY NAME", "USER DN", "STATUS"],
        rows));
}

static ProvisionWizardState SetProvisionWizardResource(
    ProvisionWizardState state,
    string resourceType,
    string value) => resourceType switch
{
    "device-pool" => state with { DevicePoolName = value },
    "phone-template" => state with { PhoneTemplateName = value },
    "security-profile" => state with { SecurityProfileName = value },
    _ => throw new InvalidOperationException($"Unknown CUCM phone resource type '{resourceType}'."),
};

static IReadOnlyList<string> ProvisionWizardArguments(string route, ProvisionWizardState state) =>
    [
        "provision",
        route,
        state.IsNewPhone.ToString(),
        state.PhoneName,
        state.Description ?? string.Empty,
        state.Product ?? string.Empty,
        state.DevicePoolName ?? string.Empty,
        state.PhoneTemplateName ?? string.Empty,
        state.SecurityProfileName ?? string.Empty,
        state.UserId ?? string.Empty,
        state.PreviousOwnerUserName ?? string.Empty,
    ];

static bool TryParseProvisionWizardState(
    IReadOnlyList<string> arguments,
    string route,
    out ProvisionWizardState state)
{
    state = new ProvisionWizardState(false, string.Empty);
    if (arguments.Count != 10 || arguments[0] != route)
    {
        return false;
    }
    state = new ProvisionWizardState(
        bool.TryParse(arguments[1], out var isNew) && isNew,
        arguments[2],
        Normalize(arguments[3]),
        Normalize(arguments[4]),
        Normalize(arguments[5]),
        Normalize(arguments[6]),
        Normalize(arguments[7]),
        Normalize(arguments[8]),
        Normalize(arguments[9]));
    return true;
}

// Sentinel used in place of a device pool name to mean "no filter, pull every phone."
static string AllDevicePoolsToken() => "*";

static bool IsAllDevicePools(string pool) => pool == AllDevicePoolsToken();

static string DevicePoolLabel(string pool) =>
    IsAllDevicePools(pool) ? "all device pools" : $"device pool '{pool}'";

// Query/export command: `vt cucm get phones` pulls CUCM phones (optionally filtered by
// device pool via the CucmService.CountPhonesByDevicePoolAsync/ListPhonesAsync(devicePoolName:)
// surface) and either displays them or writes a CSV for downstream routing-pattern analysis.
static async ValueTask<int> GetAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (context.Arguments.Count == 0)
        {
            await context.RespondAsync(new ModuleTableResponse(
                "Query CUCM data",
                ["RESOURCE", "DESCRIPTION"],
                [
                    new ModuleTableRow(
                        "phones",
                        ["Phones", "Pull phones, optionally filtered by device pool"],
                        ["get", "phones"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["phones"])
        {
            var rows = new List<ModuleTableRow>
            {
                new(
                    "all",
                    ["<All device pools>", string.Empty],
                    ["get", "phones-output", AllDevicePoolsToken()]),
            };
            await foreach (var pool in cucm.ListDevicePoolsAsync(
                cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(pool.Name))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    pool.Uuid ?? $"device-pool:{pool.Name}",
                    [Clean(pool.Name), Clean(pool.Description)],
                    ["get", "phones-output", pool.Name]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Select device pool",
                ["NAME", "DESCRIPTION"],
                rows,
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["phones-output", var outputPool] &&
            !string.IsNullOrWhiteSpace(outputPool))
        {
            await context.RespondAsync(new ModuleTableResponse(
                $"Export target for {DevicePoolLabel(outputPool)}",
                ["OPTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "console",
                        ["Console", "Display the results in this session"],
                        ["get", "phones-run", outputPool, "console"]),
                    new ModuleTableRow(
                        "file",
                        ["File", "Write a CSV file to a path you choose"],
                        ["get", "phones-file", outputPool]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["phones-file", var filePool] &&
            !string.IsNullOrWhiteSpace(filePool))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Export {DevicePoolLabel(filePool)} phones",
                "Output CSV file path",
                ["get", "phones-run", filePool, "file"]));
            return 0;
        }
        if (context.Arguments is ["phones-run", var runPool, "console"])
        {
            var records = await PullPhoneExportRecordsAsync(context, cucm, runPool);
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM phones: {DevicePoolLabel(runPool)}",
                PhoneExportColumns(),
                records.Select(ToPhoneExportRow).ToArray()));
            return 0;
        }
        if (context.Arguments is ["phones-run", var fileRunPool, "file", var filePath] &&
            !string.IsNullOrWhiteSpace(filePath))
        {
            var records = await PullPhoneExportRecordsAsync(context, cucm, fileRunPool);
            await WritePhoneExportCsvAsync(filePath, records, context.CancellationToken);
            context.Output.WriteLine(
                $"Wrote {records.Count} CUCM phone(s) for {DevicePoolLabel(fileRunPool)} to " +
                $"'{filePath}'.");
            return 0;
        }

        context.Error.WriteLine("Usage: vt cucm get phones");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static string[] PhoneExportColumns() =>
[
    "NAME", "DESCRIPTION", "PRODUCT", "MODEL", "PROTOCOL", "OWNER",
    "DEVICE POOL", "PHONE TEMPLATE", "SECURITY PROFILE", "LINES",
];

static PhoneExportRecord ToPhoneExportRecord(CucmPhone phone) =>
    new(
        phone.Name ?? string.Empty,
        phone.Description,
        phone.Product,
        phone.Model,
        phone.Protocol,
        phone.OwnerUserName,
        phone.DevicePoolName,
        phone.PhoneTemplateName,
        phone.SecurityProfileName,
        string.Join(
            "|",
            phone.Lines
                .OrderBy(line => line.Index)
                .Select(line => $"{line.Index}:{line.Pattern}@{line.RoutePartitionName}")));

static ModuleTableRow ToPhoneExportRow(PhoneExportRecord record) =>
    new(
        string.IsNullOrWhiteSpace(record.Name) ? Guid.NewGuid().ToString() : record.Name,
        [
            record.Name,
            Clean(record.Description),
            Clean(record.Product),
            Clean(record.Model),
            Clean(record.Protocol),
            Clean(record.Owner),
            Clean(record.DevicePool),
            Clean(record.PhoneTemplate),
            Clean(record.SecurityProfile),
            record.Lines,
        ]);

static async Task<List<PhoneExportRecord>> PullPhoneExportRecordsAsync(
    ModuleContext context,
    CucmService cucm,
    string pool)
{
    var poolFilter = IsAllDevicePools(pool) ? null : pool;
    await context.ReportProgressAsync("Counting CUCM phones", 0, 1);
    var availablePhones = poolFilter is null
        ? await cucm.CountPhonesAsync(context.CancellationToken)
        : await cucm.CountPhonesByDevicePoolAsync(poolFilter, context.CancellationToken);
    if (availablePhones == 0)
    {
        await context.ReportProgressAsync("No CUCM phones found", 1, 1);
    }
    else
    {
        await context.ReportProgressAsync(
            $"Loading 0 of {availablePhones} CUCM phones",
            0,
            availablePhones);
    }
    var records = new List<PhoneExportRecord>();
    await foreach (var phone in cucm.ListPhonesAsync(
        cancellationToken: context.CancellationToken,
        devicePoolName: poolFilter))
    {
        records.Add(ToPhoneExportRecord(phone));
        if (records.Count <= availablePhones)
        {
            await context.ReportProgressAsync(
                $"Loading {records.Count} of {availablePhones} CUCM phones",
                records.Count,
                availablePhones);
        }
    }
    if (records.Count != availablePhones)
    {
        await context.ReportProgressAsync($"Loaded {records.Count} CUCM phones", 1, 1);
    }
    return records;
}

static async Task WritePhoneExportCsvAsync(
    string path,
    IReadOnlyList<PhoneExportRecord> records,
    CancellationToken cancellationToken)
{
    await using var writer = new StreamWriter(path, append: false);
    await writer.WriteLineAsync(string.Join(",", PhoneExportColumns()));
    foreach (var record in records)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(string.Join(
            ",",
            CsvField(record.Name),
            CsvField(record.Description),
            CsvField(record.Product),
            CsvField(record.Model),
            CsvField(record.Protocol),
            CsvField(record.Owner),
            CsvField(record.DevicePool),
            CsvField(record.PhoneTemplate),
            CsvField(record.SecurityProfile),
            CsvField(record.Lines)));
    }
}

static string CsvField(string? value)
{
    var text = value ?? string.Empty;
    return text.Length > 0 && text.IndexOfAny([',', '"', '\n', '\r']) >= 0
        ? $"\"{text.Replace("\"", "\"\"")}\""
        : text;
}

// CRUD menu for line templates stored locally under the module's data directory (see
// LineTemplateStore). Templates defined via the 'line-templates' configuration setting are listed
// for visibility but aren't editable/deletable here, since that setting is operator-controlled
// outside the running module.
static async ValueTask<int> TemplatesAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        var store = new LineTemplateStore(context.DataDirectory);
        if (context.Arguments.Count == 0)
        {
            var stored = await store.LoadAsync(context.CancellationToken);
            var configTemplates = PhoneConfigurationChecks.ParseLineTemplates(
                context.Configuration.GetValueOrDefault("line-templates") ?? "{}");
            var rows = new List<ModuleTableRow>();
            foreach (var (name, template) in stored.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new ModuleTableRow(
                    name,
                    [name, template.Kind == LineTemplateKind.Room ? "Room" : "User", "Local"],
                    ["templates", "select", name]));
            }
            foreach (var name in configTemplates.Keys
                .Where(name => !stored.ContainsKey(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new ModuleTableRow(
                    $"config:{name}",
                    [
                        name,
                        configTemplates[name].Kind == LineTemplateKind.Room ? "Room" : "User",
                        "Config (read-only here)",
                    ]));
            }
            rows.Add(new ModuleTableRow("add", ["<Add template>", string.Empty, string.Empty], ["templates", "add"]));
            await context.RespondAsync(new ModuleTableResponse(
                "Line templates",
                ["NAME", "KIND", "SOURCE"],
                rows));
            return 0;
        }
        if (context.Arguments is ["select", var selectName])
        {
            var stored = await store.LoadAsync(context.CancellationToken);
            var template = stored.TryGetValue(selectName, out var found)
                ? found
                : throw new InvalidOperationException($"Local line template '{selectName}' was not found.");
            await context.RespondAsync(new ModuleTableResponse(
                $"Line template: {selectName}",
                ["FIELD", "VALUE"],
                [
                    new ModuleTableRow("kind", ["Kind", template.Kind == LineTemplateKind.Room ? "Room" : "User"]),
                    new ModuleTableRow(
                        "partition",
                        ["Route partition", DisplayPartition(template.RoutePartitionName)]),
                    new ModuleTableRow(
                        "dn",
                        ["Preset DN", template.Pattern is null ? "<from apply-time>" : Clean(template.Pattern)]),
                    new ModuleTableRow("alerting-name", ["Alerting name", Clean(template.AlertingName)]),
                    new ModuleTableRow("display", ["Caller ID (display)", Clean(template.Display)]),
                    new ModuleTableRow("label", ["Label", Clean(template.Label)]),
                    new ModuleTableRow(
                        "external-mask",
                        ["External phone number mask", Clean(template.ExternalPhoneNumberMask)]),
                    new ModuleTableRow("voicemail", ["Voicemail profile", Clean(template.VoiceMailProfileName)]),
                    new ModuleTableRow(
                        "associate-owner",
                        ["Associates owner", template.AssociateEndUser ? "Yes" : "No"]),
                    new ModuleTableRow(
                        "delete",
                        ["Delete", "Remove this local template"],
                        ["templates", "delete-review", selectName]),
                ]));
            return 0;
        }
        if (context.Arguments is ["delete-review", var deleteReviewName])
        {
            if (!(await store.LoadAsync(context.CancellationToken)).ContainsKey(deleteReviewName))
            {
                throw new InvalidOperationException($"Local line template '{deleteReviewName}' was not found.");
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Confirm delete",
                ["TEMPLATE", "ACTION"],
                [
                    new ModuleTableRow(
                        "confirm",
                        [deleteReviewName, "Delete this local template"],
                        ["templates", "delete", deleteReviewName]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["delete", var deleteName])
        {
            var deleted = await store.DeleteAsync(deleteName, context.CancellationToken);
            context.Output.WriteLine(deleted
                ? $"Deleted local line template '{deleteName}'."
                : $"Local line template '{deleteName}' was not found.");
            return 0;
        }
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add line template",
                "Template name",
                ["templates", "add-kind"]));
            return 0;
        }
        if (context.Arguments is ["add-kind", var addName] && !string.IsNullOrWhiteSpace(addName))
        {
            await context.RespondAsync(new ModuleTableResponse(
                $"Kind for template '{addName}'",
                ["KIND", "DETAIL"],
                [
                    new ModuleTableRow(
                        "room",
                        ["Room", "Shared room line (no owner); no DN partition (derived from building)"],
                        ["templates", "add-dn", addName, "room", string.Empty]),
                    new ModuleTableRow(
                        "user",
                        ["User", "Owner-associated line"],
                        ["templates", "add-partition", addName, "user"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["add-partition", var partitionTemplateName, var partitionKind])
        {
            var rows = new List<ModuleTableRow>
            {
                new(
                    "none",
                    ["<None>", string.Empty],
                    ["templates", "add-dn", partitionTemplateName, partitionKind, string.Empty]),
            };
            await foreach (var partition in cucm.ListRoutePartitionsAsync(
                cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(partition.Name))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    partition.Uuid ?? $"partition:{partition.Name}",
                    [Clean(partition.Name), Clean(partition.Description)],
                    ["templates", "add-dn", partitionTemplateName, partitionKind, partition.Name]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Route partition for '{partitionTemplateName}'",
                ["NAME", "DESCRIPTION"],
                rows,
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["add-dn", var dnTemplateName, var dnKind, var dnPartitionRaw])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Directory number for '{dnTemplateName}'",
                "Preset 4-digit DN (leave blank to use whatever DN is assigned/created at apply time)",
                ["templates", "add-alerting-name", dnTemplateName, dnKind, dnPartitionRaw],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "add-alerting-name", var alertingNameTemplateName, var alertingNameKind,
                var alertingNamePartitionRaw, var alertingNameDnRaw,
            ])
        {
            if (!string.IsNullOrWhiteSpace(alertingNameDnRaw) &&
                !(alertingNameDnRaw.Length == 4 && alertingNameDnRaw.All(char.IsAsciiDigit)))
            {
                throw new InvalidOperationException($"'{alertingNameDnRaw}' is not a valid 4-digit DN.");
            }
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Alerting name for '{alertingNameTemplateName}'",
                "Alerting name (tokens: {room} {building} {pattern} {phoneName} {lineIndex} " +
                    "{devicePoolName} {userDisplayName} {userId})",
                [
                    "templates", "add-display", alertingNameTemplateName, alertingNameKind,
                    alertingNamePartitionRaw, alertingNameDnRaw,
                ],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "add-display", var displayTemplateName, var displayKind, var displayPartitionRaw,
                var displayDnRaw, var displayAlertingNameRaw,
            ])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Caller ID (display) for '{displayTemplateName}'",
                "Caller ID display",
                [
                    "templates", "add-label", displayTemplateName, displayKind, displayPartitionRaw, displayDnRaw,
                    displayAlertingNameRaw,
                ],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "add-label", var labelTemplateName, var labelKind, var labelPartitionRaw, var labelDnRaw,
                var labelAlertingNameRaw, var labelDisplayRaw,
            ])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Label for '{labelTemplateName}'",
                "Line text label",
                [
                    "templates", "add-external-mask", labelTemplateName, labelKind, labelPartitionRaw, labelDnRaw,
                    labelAlertingNameRaw, labelDisplayRaw,
                ],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "add-external-mask", var maskTemplateName, var maskKind, var maskPartitionRaw, var maskDnRaw,
                var maskAlertingNameRaw, var maskDisplayRaw, var maskLabelRaw,
            ])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"External phone number mask for '{maskTemplateName}'",
                "External phone number mask (e.g. 555XXXX)",
                [
                    "templates", "add-voicemail", maskTemplateName, maskKind, maskPartitionRaw, maskDnRaw,
                    maskAlertingNameRaw, maskDisplayRaw, maskLabelRaw,
                ],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "add-voicemail", var voicemailTemplateName, var voicemailKind, var voicemailPartitionRaw,
                var voicemailDnRaw, var voicemailAlertingNameRaw, var voicemailDisplayRaw,
                var voicemailLabelRaw, var voicemailMaskRaw,
            ])
        {
            var voicemailRows = new List<ModuleTableRow>
            {
                new(
                    "none",
                    ["<None>", string.Empty],
                    [
                        "templates", "add-associate", voicemailTemplateName, voicemailKind, voicemailPartitionRaw,
                        voicemailDnRaw, voicemailAlertingNameRaw, voicemailDisplayRaw, voicemailLabelRaw,
                        voicemailMaskRaw, string.Empty,
                    ]),
            };
            await foreach (var profile in cucm.ListVoiceMailProfilesAsync(
                cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    continue;
                }
                voicemailRows.Add(new ModuleTableRow(
                    profile.Uuid ?? $"voicemail-profile:{profile.Name}",
                    [Clean(profile.Name), Clean(profile.Description)],
                    [
                        "templates", "add-associate", voicemailTemplateName, voicemailKind, voicemailPartitionRaw,
                        voicemailDnRaw, voicemailAlertingNameRaw, voicemailDisplayRaw, voicemailLabelRaw,
                        voicemailMaskRaw, profile.Name,
                    ]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Voicemail profile for '{voicemailTemplateName}'",
                ["NAME", "DESCRIPTION"],
                voicemailRows,
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            [
                "add-associate", var associateTemplateName, var associateKind, var associatePartitionRaw,
                var associateDnRaw, var associateAlertingNameRaw, var associateDisplayRaw, var associateLabelRaw,
                var associateMaskRaw, var associateVoicemailRaw,
            ])
        {
            if (associateKind != "user")
            {
                await RespondWithTemplateReviewAsync(
                    context, associateTemplateName, associateKind, associatePartitionRaw, associateDnRaw,
                    associateAlertingNameRaw, associateDisplayRaw, associateLabelRaw, associateMaskRaw,
                    associateVoicemailRaw, associateEndUserRaw: "false");
                return 0;
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Associate owner for '{associateTemplateName}'",
                ["OPTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "yes",
                        ["Yes", "Prompt for and set an owner when this template is applied"],
                        [
                            "templates", "add-review", associateTemplateName, associateKind, associatePartitionRaw,
                            associateDnRaw, associateAlertingNameRaw, associateDisplayRaw, associateLabelRaw,
                            associateMaskRaw, associateVoicemailRaw, "true",
                        ]),
                    new ModuleTableRow(
                        "no",
                        ["No", "Do not change the owner"],
                        [
                            "templates", "add-review", associateTemplateName, associateKind, associatePartitionRaw,
                            associateDnRaw, associateAlertingNameRaw, associateDisplayRaw, associateLabelRaw,
                            associateMaskRaw, associateVoicemailRaw, "false",
                        ]),
                ]));
            return 0;
        }
        if (context.Arguments is
            [
                "add-review", var reviewTemplateName, var reviewKind, var reviewPartitionRaw, var reviewDnRaw,
                var reviewAlertingNameRaw, var reviewDisplayRaw, var reviewLabelRaw, var reviewMaskRaw,
                var reviewVoicemailRaw, var reviewAssociateRaw,
            ])
        {
            await RespondWithTemplateReviewAsync(
                context, reviewTemplateName, reviewKind, reviewPartitionRaw, reviewDnRaw, reviewAlertingNameRaw,
                reviewDisplayRaw, reviewLabelRaw, reviewMaskRaw, reviewVoicemailRaw, reviewAssociateRaw);
            return 0;
        }
        if (context.Arguments is
            [
                "add-save", var saveTemplateName, var saveKind, var savePartitionRaw, var saveDnRaw,
                var saveAlertingNameRaw, var saveDisplayRaw, var saveLabelRaw, var saveMaskRaw,
                var saveVoicemailRaw, var saveAssociateRaw,
            ])
        {
            var template = new LineTemplate(
                saveKind == "room" ? LineTemplateKind.Room : LineTemplateKind.User,
                Normalize(savePartitionRaw),
                Normalize(saveAlertingNameRaw),
                Normalize(saveDisplayRaw),
                Normalize(saveLabelRaw),
                Normalize(saveMaskRaw),
                bool.TryParse(saveAssociateRaw, out var associate) && associate,
                Normalize(saveVoicemailRaw),
                Normalize(saveDnRaw));
            await store.SaveAsync(saveTemplateName, template, context.CancellationToken);
            context.Output.WriteLine($"Saved local line template '{saveTemplateName}'.");
            return 0;
        }

        context.Error.WriteLine("Usage: vt cucm templates [add|select <name>|delete <name>]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static ValueTask RespondWithTemplateReviewAsync(
    ModuleContext context,
    string templateName,
    string kind,
    string partitionRaw,
    string dnRaw,
    string alertingNameRaw,
    string displayRaw,
    string labelRaw,
    string maskRaw,
    string voicemailRaw,
    string associateEndUserRaw) =>
    context.RespondAsync(new ModuleTableResponse(
        $"Review template '{templateName}'",
        [
            "NAME", "KIND", "PARTITION", "DN", "ALERTING NAME", "CALLER ID", "LABEL", "EXTERNAL MASK",
            "VOICEMAIL", "ASSOCIATES OWNER",
        ],
        [
            new ModuleTableRow(
                "submit",
                [
                    templateName,
                    kind == "room" ? "Room" : "User",
                    DisplayPartition(Normalize(partitionRaw)),
                    string.IsNullOrWhiteSpace(dnRaw) ? "<from apply-time>" : dnRaw,
                    string.IsNullOrWhiteSpace(alertingNameRaw) ? "<none>" : alertingNameRaw,
                    string.IsNullOrWhiteSpace(displayRaw) ? "<none>" : displayRaw,
                    string.IsNullOrWhiteSpace(labelRaw) ? "<none>" : labelRaw,
                    string.IsNullOrWhiteSpace(maskRaw) ? "<none>" : maskRaw,
                    string.IsNullOrWhiteSpace(voicemailRaw) ? "<none>" : voicemailRaw,
                    kind == "user" && associateEndUserRaw == "true" ? "Yes" : "No",
                ],
                [
                    "templates", "add-save", templateName, kind, partitionRaw, dnRaw, alertingNameRaw, displayRaw,
                    labelRaw, maskRaw, voicemailRaw, associateEndUserRaw,
                ]),
        ],
        SubmitMode: ModuleTableSubmitMode.Save));

static async ValueTask<int> UsersAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            var dids = await new UserDidStore(context.DataDirectory)
                .LoadAsync(context.CancellationToken);
            var rows = new List<ModuleTableRow>();
            await foreach (var user in cucm.ListUsersAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var id = user.UserId ?? user.Uuid ?? $"user-{rows.Count + 1}";
                var userExtension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
                rows.Add(new ModuleTableRow(
                    id,
                    [
                        Clean(user.UserId),
                        Clean(user.DisplayName),
                        Clean(user.Email),
                        Clean(user.TelephoneNumber),
                        Clean(userExtension),
                        UserDnStatus(user.UserId, userExtension, dids),
                        Clean(string.Join(",", user.AssociatedDevices)),
                    ],
                    string.IsNullOrWhiteSpace(user.UserId)
                        ? null
                        : ["users", "select", user.UserId]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM users",
                ["USER ID", "DISPLAY NAME", "EMAIL", "LDAP PHONE", "USER DN", "DN STATUS", "DEVICES"],
                rows));
            return 0;
        }
        if (context.Arguments is ["select", var userId])
        {
            var user = await cucm.GetUserAsync(userId, context.CancellationToken) ??
                throw new InvalidOperationException($"CUCM user '{userId}' was not found.");
            var userExtension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
            var did = await FindUserDidAsync(context, userExtension);
            var rows = user.AssociatedDevices
                .Select(device => new ModuleTableRow(
                    device,
                    ["Phone", device],
                    ["phones", "select", device]))
                .Prepend(new ModuleTableRow(
                    "assign-phone",
                    [
                        "Assign phone and DN",
                        UserDnStatus(user.UserId, userExtension, did is null ? [] : [did]),
                    ],
                    did is { Assignment: null }
                        ? ["users", "phones", userId]
                        : null))
                .Prepend(new ModuleTableRow(
                    "user-dn",
                    ["LDAP user DN", Clean(userExtension)]))
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
        if (context.Arguments is ["phones", var phoneUserId])
        {
            var user = await RequireUserAsync(cucm, phoneUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidForUserAsync(context, user);
            var rows = new List<ModuleTableRow>();
            await foreach (var phone in cucm.ListPhonesAsync(
                cancellationToken: context.CancellationToken))
            {
                var phoneName = phone.Name;
                if (string.IsNullOrWhiteSpace(phoneName))
                {
                    continue;
                }
                var ownedByAnother = !string.IsNullOrWhiteSpace(phone.OwnerUserName) &&
                    !phone.OwnerUserName.Equals(phoneUserId, StringComparison.OrdinalIgnoreCase);
                rows.Add(new ModuleTableRow(
                    phoneName,
                    [
                        phoneName,
                        Clean(phone.Description),
                        Clean(phone.OwnerUserName),
                        ownedByAnother
                            ? $"Reassign from {phone.OwnerUserName} to {did.Pattern}"
                            : $"Assign {did.Pattern}",
                    ],
                    ["users", "phone", phoneUserId, phoneName]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Select a phone for {user.DisplayName ?? phoneUserId} ({did.Pattern})",
                ["PHONE", "DESCRIPTION", "OWNER", "STATUS"],
                rows));
            return 0;
        }
        if (context.Arguments is ["phone", var slotUserId, var slotPhoneName])
        {
            var user = await RequireUserAsync(cucm, slotUserId, context.CancellationToken);
            _ = await RequireAvailableUserDidForUserAsync(context, user);
            _ = await RequirePhoneAsync(cucm, slotPhoneName, context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Assign {UserDidStore.NormalizeUserExtension(user.TelephoneNumber)} to {slotPhoneName}",
                "Line slot index",
                ["users", "review", slotUserId, slotPhoneName],
                "1"));
            return 0;
        }
        if (context.Arguments is
            ["review", var reviewUserId, var reviewPhoneName, var reviewSlotText] &&
            int.TryParse(reviewSlotText, out var reviewSlot) && reviewSlot > 0)
        {
            var user = await RequireUserAsync(cucm, reviewUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidForUserAsync(context, user);
            var phone = await RequirePhoneAsync(cucm, reviewPhoneName, context.CancellationToken);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                did.RoutePartitionName,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            var reviewUserResolvedPartition = existing?.RoutePartitionName ?? did.RoutePartitionName;
            var currentLine = phone.Lines.FirstOrDefault(line => line.Index == reviewSlot);
            var alreadyAssociated = user.AssociatedDevices.Contains(
                reviewPhoneName,
                StringComparer.OrdinalIgnoreCase);
            await context.RespondAsync(new ModuleTableResponse(
                "Review user phone assignment",
                ["USER", "USER DN", "PARTITION", "PHONE", "SLOT", "CURRENT", "ASSOCIATION", "DN ACTION", "OWNER ACTION"],
                [
                    new ModuleTableRow(
                        "assign",
                        [
                            user.DisplayName ?? reviewUserId,
                            did.Pattern,
                            DisplayPartition(reviewUserResolvedPartition),
                            reviewPhoneName,
                            reviewSlot.ToString(),
                            Clean(currentLine?.Pattern),
                            alreadyAssociated ? "Keep existing" : "Add to user",
                            existing is null ? "Create in CUCM" : "Use existing CUCM DN",
                            OwnerActionLabel(phone.OwnerUserName, reviewUserId),
                        ],
                        [
                            "users", "assign", reviewUserId, reviewPhoneName, reviewSlot.ToString(),
                            did.Pattern, did.RoutePartitionName ?? string.Empty,
                            reviewUserResolvedPartition ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["assign", var assignmentUserId, var assignmentPhoneName, var assignmentSlotText,
                var assignmentPattern, var inventoryPartition, var assignmentUserResolvedPartition] &&
            int.TryParse(assignmentSlotText, out var assignmentSlot) && assignmentSlot > 0)
        {
            var user = await RequireUserAsync(cucm, assignmentUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidAsync(
                context,
                assignmentPattern,
                inventoryPartition);
            if (!string.Equals(
                UserDidStore.NormalizeUserExtension(user.TelephoneNumber),
                did.Pattern,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CUCM user '{assignmentUserId}' no longer maps to DN '{did.Pattern}'.");
            }
            var phone = await RequirePhoneAsync(
                cucm,
                assignmentPhoneName,
                context.CancellationToken);
            var previousOwnerUserId = Normalize(phone.OwnerUserName);
            var replacingOwner = previousOwnerUserId is not null &&
                !previousOwnerUserId.Equals(assignmentUserId, StringComparison.OrdinalIgnoreCase);
            var effectivePartition = Normalize(assignmentUserResolvedPartition);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                effectivePartition,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            if (existing is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        did.Pattern,
                        effectivePartition,
                        did.Description,
                        did.CallingSearchSpaceName,
                        did.VoiceMailProfileName),
                    context.CancellationToken);
            }

            var previousDevices = user.AssociatedDevices.ToArray();
            var addAssociation = !previousDevices.Contains(
                assignmentPhoneName,
                StringComparer.OrdinalIgnoreCase);
            if (addAssociation)
            {
                await cucm.UpdateUserAssociatedDevicesAsync(
                    assignmentUserId,
                    previousDevices.Append(assignmentPhoneName),
                    context.CancellationToken);
            }
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberToUserAsync(
                    assignmentPhoneName,
                    assignmentSlot,
                    did.Pattern,
                    effectivePartition,
                    assignmentUserId,
                    context.CancellationToken);
            }
            catch (Exception assignmentException)
            {
                if (addAssociation)
                {
                    try
                    {
                        await cucm.UpdateUserAssociatedDevicesAsync(
                            assignmentUserId,
                            previousDevices,
                            context.CancellationToken);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "The phone update failed, and the user-device association could not " +
                            "be rolled back.",
                            new AggregateException(assignmentException, rollbackException));
                    }
                }
                throw;
            }
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern,
                did.RoutePartitionName,
                assignmentPhoneName,
                assignmentSlot,
                assignmentUserId,
                effectivePartition,
                context.CancellationToken);
            var assignmentWarnings = new List<string>();
            if (replacingOwner)
            {
                try
                {
                    await RemovePhoneFromPreviousOwnerAsync(
                        cucm,
                        previousOwnerUserId!,
                        assignmentPhoneName,
                        context.CancellationToken);
                }
                catch (Exception cleanupException)
                {
                    assignmentWarnings.Add(
                        $"could not remove '{assignmentPhoneName}' from previous owner " +
                        $"'{previousOwnerUserId}' associated devices: {cleanupException.Message}");
                }
            }
            try
            {
                await EnsureRoomLineAsync(cucm, assignmentPhoneName, context.CancellationToken);
            }
            catch (Exception roomException)
            {
                assignmentWarnings.Add($"could not ensure a room DN on line 3: {roomException.Message}");
            }
            var assignmentComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, assignmentPhoneName, null, context.CancellationToken);
            context.Output.WriteLine(
                $"Assigned phone '{assignmentPhoneName}' and DN '{did.Pattern}' " +
                $"to CUCM user '{assignmentUserId}'." +
                (replacingOwner ? $" Replaced previous owner '{previousOwnerUserId}'." : string.Empty) +
                $" Description: '{assignmentComposedDescription}'." +
                (assignmentWarnings.Count == 0
                    ? string.Empty
                    : " WARNING: " + string.Join(" ", assignmentWarnings)));
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

static string UserDnStatus(
    string? userId,
    string? extension,
    IReadOnlyList<UserDid> dids)
{
    if (extension is null)
    {
        return "No four-digit LDAP DN";
    }
    var did = dids.FirstOrDefault(candidate =>
        candidate.Pattern.Equals(extension, StringComparison.Ordinal));
    if (did is null)
    {
        return "Not in local pool";
    }
    if (did.Assignment is null)
    {
        return "Available";
    }
    return string.Equals(did.Assignment.UserId, userId, StringComparison.OrdinalIgnoreCase)
        ? $"Assigned to {did.Assignment.PhoneName}"
        : $"Assigned to {did.Assignment.UserId ?? did.Assignment.PhoneName}";
}

static async Task<UserDid?> FindUserDidAsync(ModuleContext context, string? extension)
{
    if (extension is null)
    {
        return null;
    }
    var matches = (await new UserDidStore(context.DataDirectory)
        .LoadAsync(context.CancellationToken))
        .Where(did => did.Pattern.Equals(extension, StringComparison.Ordinal))
        .ToArray();
    return matches.Length switch
    {
        0 => null,
        1 => matches[0],
        _ => throw new InvalidOperationException(
            $"User DN '{extension}' appears more than once in the local inventory."),
    };
}

static async Task<UserDid> RequireAvailableUserDidForUserAsync(
    ModuleContext context,
    CucmUser user,
    string? phoneName = null,
    int? lineIndex = null)
{
    var extension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber) ??
        throw new InvalidOperationException(
            $"CUCM user '{user.UserId}' does not have a usable four-digit LDAP telephone number.");
    var did = await FindUserDidAsync(context, extension) ??
        throw new InvalidOperationException(
            $"CUCM user '{user.UserId}' maps to DN '{extension}', which is not in the local pool.");
    if (did.Assignment is { } assignment &&
        !(phoneName is not null &&
          lineIndex is not null &&
          assignment.PhoneName.Equals(phoneName, StringComparison.OrdinalIgnoreCase) &&
          assignment.LineIndex == lineIndex &&
          string.Equals(assignment.UserId, user.UserId, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException(
            $"User DN '{extension}' is already assigned to '{did.Assignment.PhoneName}'.");
    }
    return did;
}

static async Task<CucmUser> RequireUserAsync(
    CucmService cucm,
    string userId,
    CancellationToken cancellationToken) =>
    await cucm.GetUserAsync(userId, cancellationToken) ??
        throw new InvalidOperationException($"CUCM user '{userId}' was not found.");

// Describes what will happen to a phone's current owner when it's (re)assigned to userId,
// for display on review screens before the user confirms with Ctrl+Enter/Cmd+Enter.
static string OwnerActionLabel(string? currentOwner, string userId)
{
    if (string.IsNullOrWhiteSpace(currentOwner))
    {
        return "Set new owner";
    }
    return currentOwner.Equals(userId, StringComparison.OrdinalIgnoreCase)
        ? "Keep current owner"
        : $"Replace '{currentOwner}'";
}

// Removes a phone from a previous owner's associated devices after it has been reassigned to
// a new owner in CUCM, so the old owner doesn't retain a stale device association.
static async Task RemovePhoneFromPreviousOwnerAsync(
    CucmService cucm,
    string previousOwnerUserId,
    string phoneName,
    CancellationToken cancellationToken)
{
    var previousOwner = await cucm.GetUserAsync(previousOwnerUserId, cancellationToken);
    if (previousOwner is null)
    {
        return;
    }
    var remainingDevices = previousOwner.AssociatedDevices
        .Where(device => !device.Equals(phoneName, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (remainingDevices.Length != previousOwner.AssociatedDevices.Count)
    {
        await cucm.UpdateUserAssociatedDevicesAsync(
            previousOwnerUserId,
            remainingDevices,
            cancellationToken);
    }
}

// School convention: line 1 carries the assigned user's DN, line 3 carries a shared room DN.
// Existing line 3 assignments are left untouched. There is no per-phone/location room-DID
// source yet, so a missing line 3 gets a placeholder room DN rather than blocking the
// phone/user assignment on unknown room data.
static async Task EnsureRoomLineAsync(
    CucmService cucm,
    string phoneName,
    CancellationToken cancellationToken)
{
    var phone = await RequirePhoneAsync(cucm, phoneName, cancellationToken);
    var lineThree = phone.Lines.FirstOrDefault(line => line.Index == 3);
    if (!string.IsNullOrWhiteSpace(lineThree?.Pattern))
    {
        return;
    }
    var roomPattern = RoomDidPlaceholderPattern();
    var existingRoomDn = await cucm.GetDirectoryNumberAsync(roomPattern, null, cancellationToken);
    if (existingRoomDn is null)
    {
        await cucm.AddDirectoryNumberAsync(
            new CucmDirectoryNumberCreateRequest(roomPattern),
            cancellationToken);
    }
    await cucm.AssignPhoneLineDirectoryNumberAsync(
        phoneName,
        3,
        roomPattern,
        null,
        cancellationToken);
}

// Placeholder room DN used to populate an empty line 3 until a real per-phone/location room
// DID source exists.
static string RoomDidPlaceholderPattern() => "89898989";

static async ValueTask<int> PhonesAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            await context.ReportProgressAsync("Counting CUCM phones", 0, 1);
            var availablePhones = await cucm.CountPhonesAsync(context.CancellationToken);
            var totalPhones = Math.Min(availablePhones, maxRecords ?? availablePhones);
            if (totalPhones == 0)
            {
                await context.ReportProgressAsync("No CUCM phones found", 1, 1);
            }
            else
            {
                await context.ReportProgressAsync(
                    $"Loading 0 of {totalPhones} CUCM phones",
                    0,
                    totalPhones);
            }
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
                if (rows.Count <= totalPhones)
                {
                    await context.ReportProgressAsync(
                        $"Loading {rows.Count} of {totalPhones} CUCM phones",
                        rows.Count,
                        totalPhones);
                }
            }
            if (rows.Count != totalPhones)
            {
                await context.ReportProgressAsync(
                    $"Loaded {rows.Count} CUCM phones",
                    1,
                    1);
            }
            rows.Add(new ModuleTableRow(
                "add",
                ["<Add phone>", "Create a new, unregistered CUCM phone shell from scratch", "", "", ""],
                ["phones", "add"]));
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
                    new ModuleTableRow(
                        "assign-user-did",
                        ["Assign user DN to slot", "Select an available user DN for a line index"],
                        ["phones", "assign-slot", phoneName]),
                    new ModuleTableRow(
                        "checks",
                        ["Check configuration", "Validate this phone against a named profile"],
                        ["phones", "check", phoneName]),
                    new ModuleTableRow(
                        "apply-classroom",
                        [
                            "Apply classroom template",
                            "Set building, room number, and user; the rest auto-applies",
                        ],
                        ["phones", "classroom", phoneName]),
                    new ModuleTableRow(
                        "edit",
                        ["Edit", "Update description, device pool, and owner"],
                        ["phones", "edit", phoneName]),
                ]));
            return 0;
        }
        if (context.Arguments is ["assign-slot", var phoneNameForSlot])
        {
            var phoneForSlotList = await RequirePhoneAsync(cucm, phoneNameForSlot, context.CancellationToken);
            var nextSlotIndex = phoneForSlotList.Lines.Count == 0
                ? 1
                : phoneForSlotList.Lines.Max(line => line.Index) + 1;
            var slotRows = phoneForSlotList.Lines
                .OrderBy(line => line.Index)
                .Select(line => new ModuleTableRow(
                    $"slot-{line.Index}",
                    [
                        line.Index.ToString(),
                        string.IsNullOrWhiteSpace(line.Pattern) ? "<Empty>" : Clean(line.Pattern),
                        Clean(line.RoutePartitionName),
                        Clean(line.Label),
                    ],
                    ["phones", "slot", phoneNameForSlot, line.Index.ToString()]))
                .ToList();
            slotRows.Add(new ModuleTableRow(
                "slot-next",
                [nextSlotIndex.ToString(), "<Add new line>", string.Empty, string.Empty],
                ["phones", "slot", phoneNameForSlot, nextSlotIndex.ToString()]));
            slotRows.Add(new ModuleTableRow(
                "slot-custom",
                ["Custom", "Enter a specific line index", string.Empty, string.Empty],
                ["phones", "assign-slot-custom", phoneNameForSlot]));
            await context.RespondAsync(new ModuleTableResponse(
                $"Assign user DN on {phoneForSlotList.Name ?? phoneNameForSlot}",
                ["INDEX", "CURRENT NUMBER", "PARTITION", "LABEL"],
                slotRows));
            return 0;
        }
        if (context.Arguments is ["assign-slot-custom", var phoneNameForSlotCustom])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Assign user DID on {phoneNameForSlotCustom}",
                "Line slot index",
                ["phones", "slot", phoneNameForSlotCustom]));
            return 0;
        }
        if (context.Arguments is ["slot", var phoneNameForSlotSelection, var slotIndexText] &&
            int.TryParse(slotIndexText, out var slotIndex) && slotIndex > 0)
        {
            var phoneForSlotSelection = await RequirePhoneAsync(
                cucm,
                phoneNameForSlotSelection,
                context.CancellationToken);
            if (!phoneForSlotSelection.Lines.Any(line => line.Index == slotIndex))
            {
                context.Output.WriteLine(
                    $"Note: line {slotIndex} doesn't exist yet on {phoneNameForSlotSelection}. CUCM " +
                    "only accepts a new line at a position defined as a Line-type button in the " +
                    $"phone's button template ('{Clean(phoneForSlotSelection.PhoneTemplateName)}'). " +
                    "If the assignment below fails, change the phone's button template via " +
                    "'phones edit' to one with more line positions, then retry.");
            }
            await RespondWithAvailableUserDidsAsync(
                context,
                phoneNameForSlotSelection,
                slotIndex);
            return 0;
        }
        if (context.Arguments is ["check", var phoneNameForChecks])
        {
            await context.RespondAsync(new ModuleTableResponse(
                $"Configuration checks for {phoneNameForChecks}",
                ["PROFILE", "DETAIL"],
                [
                    new ModuleTableRow(
                        "basic-room",
                        ["Basic room", "No checks configured yet"],
                        ["phones", "check", phoneNameForChecks, "basic-room"]),
                    new ModuleTableRow(
                        "classroom",
                        ["Classroom", "Validate assigned-user and room lines"],
                        ["phones", "check", phoneNameForChecks, "classroom"]),
                    new ModuleTableRow(
                        "room-routing",
                        ["Room routing", "Validate the room line's partition against its building"],
                        ["phones", "check", phoneNameForChecks, "room-routing"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["check", var phoneNameToCheck, var profileName] &&
            PhoneConfigurationChecks.TryParseProfile(profileName, out var profile))
        {
            await context.ReportProgressAsync("Loading CUCM phone", 0, 3);
            var phone = await RequirePhoneAsync(cucm, phoneNameToCheck, context.CancellationToken);
            IReadOnlyList<PhoneCheckResult> results;
            if (profile == PhoneConfigurationProfile.RoomRouting)
            {
                await context.ReportProgressAsync("Resolving building patterns", 1, 3);
                var buildingPatterns = PhoneConfigurationChecks.ParseBuildingPatterns(
                    context.Configuration.GetValueOrDefault("building-patterns") ?? "{}");
                await context.ReportProgressAsync("Evaluating room routing", 2, 3);
                results = PhoneConfigurationChecks.EvaluateRoomRouting(phone, buildingPatterns);
            }
            else
            {
                await context.ReportProgressAsync("Resolving phone assignment", 1, 3);
                var assignment = PhoneConfigurationChecks.ResolvePlaceholderAssignment(
                    phone,
                    context.Configuration.GetValueOrDefault("phone-check-placeholder") ?? "{}");
                var roomPartitions = PhoneConfigurationChecks.ParseRoomPartitions(
                    context.Configuration.GetValueOrDefault("room-partitions") ?? "{}");
                await context.ReportProgressAsync("Evaluating classroom configuration", 2, 3);
                results = PhoneConfigurationChecks.Evaluate(
                    profile,
                    phone,
                    assignment,
                    roomPartitions);
            }
            await context.ReportProgressAsync("Phone configuration check complete", 3, 3);
            await context.RespondAsync(new ModuleTableResponse(
                $"{FormatProfile(profile)} check: {phone.Name ?? phoneNameToCheck}",
                ["CHECK", "EXPECTED", "ACTUAL", "RESULT", "DETAIL"],
                results.Select((result, index) => new ModuleTableRow(
                    $"check-{index + 1}",
                    [
                        result.Name,
                        result.Expected,
                        result.Actual,
                        PhoneConfigurationChecks.DisplayStatus(result.Status),
                        result.Detail,
                    ])).ToArray(),
                Selectable: false,
                Searchable: false));
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
                ["phones", "line", phoneNameForLines, line.Index.ToString()])).ToList();
            var nextLineIndex = phone.Lines.Count == 0 ? 1 : phone.Lines.Max(line => line.Index) + 1;
            rows.Add(new ModuleTableRow(
                $"{phoneNameForLines}:add",
                [nextLineIndex.ToString(), "<Add new line>", string.Empty, string.Empty, string.Empty],
                ["phones", "line", phoneNameForLines, nextLineIndex.ToString()]));
            await context.RespondAsync(new ModuleTableResponse(
                $"Numbers on {phone.Name ?? phoneNameForLines}",
                ["INDEX", "NUMBER", "PARTITION", "LABEL", "DISPLAY"],
                rows));
            return 0;
        }
        if (context.Arguments is ["line", var phoneNameForLine, var indexText] &&
            int.TryParse(indexText, out var lineIndex) && lineIndex > 0)
        {
            var linePhone = await RequirePhoneAsync(cucm, phoneNameForLine, context.CancellationToken);
            var line = linePhone.Lines.FirstOrDefault(candidate => candidate.Index == lineIndex);
            var lineRows = new List<ModuleTableRow>
            {
                new(
                    "set-dn",
                    ["Set directory number", Clean(line?.Pattern)],
                    ["phones", "dn", phoneNameForLine, lineIndex.ToString()]),
                new(
                    "assign-user-did",
                    ["Assign available user DN", "Select from the local approved inventory"],
                    ["phones", "dids", phoneNameForLine, lineIndex.ToString()]),
                new(
                    "assign-room-did",
                    ["Assign room DN", "Create or reuse a room DN by building + room number"],
                    ["phones", "room-building", phoneNameForLine, lineIndex.ToString()]),
                new(
                    "dn-options",
                    ["Set DN options", "Per-field editor: partition, alerting name, caller ID, " +
                        "label, external mask, owner (F1 to apply a template)"],
                    ["phones", "dn-options", phoneNameForLine, lineIndex.ToString()]),
            };
            // Label/caller ID/removal require an existing line appearance with a DN in CUCM; a
            // brand-new (not-yet-assigned) index has nothing there yet to update or remove.
            if (line is not null && !string.IsNullOrWhiteSpace(line.Pattern))
            {
                lineRows.Add(new ModuleTableRow(
                    "set-label",
                    ["Set label", Clean(line.Label)],
                    ["phones", "label", phoneNameForLine, lineIndex.ToString()]));
                lineRows.Add(new ModuleTableRow(
                    "set-caller-id",
                    ["Set caller ID", Clean(line.Display)],
                    ["phones", "caller-id", phoneNameForLine, lineIndex.ToString()]));
                lineRows.Add(new ModuleTableRow(
                    "remove-dn",
                    ["Remove directory number", $"Clear {line.Pattern}"],
                    ["phones", "remove-line-review", phoneNameForLine, lineIndex.ToString()]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"{line?.Pattern ?? $"Line {lineIndex} (new)"} on {phoneNameForLine}",
                ["ACTION", "CURRENT VALUE"],
                lineRows));
            return 0;
        }
        if (context.Arguments is ["dn-options", var phoneNameForDnOptions, var dnOptionsIndexText] &&
            int.TryParse(dnOptionsIndexText, out var dnOptionsIndex) && dnOptionsIndex > 0)
        {
            var dnOptionsPhone = await RequirePhoneAsync(cucm, phoneNameForDnOptions, context.CancellationToken);
            var dnOptionsLine = dnOptionsPhone.Lines.FirstOrDefault(candidate => candidate.Index == dnOptionsIndex);
            var dnOptionsRows = new List<ModuleTableRow>
            {
                new(
                    "dn",
                    ["Directory number & partition", $"{Clean(dnOptionsLine?.Pattern)} " +
                        $"({DisplayPartition(dnOptionsLine?.RoutePartitionName)})"],
                    ["phones", "dn", phoneNameForDnOptions, dnOptionsIndex.ToString()]),
            };
            // Alerting name/caller ID/label/external mask/owner only make sense once a DN is
            // assigned; Apply Template (F1) is how a brand-new line gets one of those from scratch.
            if (dnOptionsLine is not null && !string.IsNullOrWhiteSpace(dnOptionsLine.Pattern))
            {
                var dnOptionsDn = await cucm.GetDirectoryNumberAsync(
                    dnOptionsLine.Pattern,
                    dnOptionsLine.RoutePartitionName,
                    context.CancellationToken);
                dnOptionsRows.Add(new ModuleTableRow(
                    "alerting-name",
                    ["Alerting name", Clean(dnOptionsDn?.AlertingName)],
                    ["phones", "alerting-name", phoneNameForDnOptions, dnOptionsIndex.ToString()]));
                dnOptionsRows.Add(new ModuleTableRow(
                    "caller-id",
                    ["Caller ID (display)", Clean(dnOptionsLine.Display)],
                    ["phones", "caller-id", phoneNameForDnOptions, dnOptionsIndex.ToString()]));
                dnOptionsRows.Add(new ModuleTableRow(
                    "label",
                    ["Line text label", Clean(dnOptionsLine.Label)],
                    ["phones", "label", phoneNameForDnOptions, dnOptionsIndex.ToString()]));
                dnOptionsRows.Add(new ModuleTableRow(
                    "external-mask",
                    ["External phone number mask", Clean(dnOptionsLine.ExternalPhoneNumberMask)],
                    ["phones", "external-mask", phoneNameForDnOptions, dnOptionsIndex.ToString()]));
                dnOptionsRows.Add(new ModuleTableRow(
                    "owner",
                    ["Owner (associated end user)", Clean(dnOptionsPhone.OwnerUserName)],
                    ["phones", "dn-owner", phoneNameForDnOptions, dnOptionsIndex.ToString()]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"DN options: {dnOptionsLine?.Pattern ?? $"line {dnOptionsIndex} (new)"} on " +
                    $"{phoneNameForDnOptions}",
                ["FIELD", "CURRENT VALUE"],
                dnOptionsRows,
                QuickActions:
                [
                    new ModuleQuickAction(
                        "F1",
                        "Apply template",
                        ["phones", "apply-template", phoneNameForDnOptions, dnOptionsIndex.ToString()]),
                ]));
            return 0;
        }
        if (context.Arguments is ["dn", var phoneNameForDnPrompt, var dnIndexText] &&
            int.TryParse(dnIndexText, out var dnIndex) && dnIndex > 0)
        {
            var dnPhone = await RequirePhoneAsync(cucm, phoneNameForDnPrompt, context.CancellationToken);
            var line = dnPhone.Lines.FirstOrDefault(candidate => candidate.Index == dnIndex);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set directory number for line {dnIndex} on {phoneNameForDnPrompt}",
                "Directory number",
                ["phones", "set-dn-partition", phoneNameForDnPrompt, dnIndex.ToString()],
                line?.Pattern));
            return 0;
        }
        if (context.Arguments is
            ["set-dn-partition", var dnPartitionPhoneName, var dnPartitionIndexText, var dnPartitionPattern] &&
            int.TryParse(dnPartitionIndexText, out var dnPartitionIndex) && dnPartitionIndex > 0 &&
            !string.IsNullOrWhiteSpace(dnPartitionPattern))
        {
            var dnPartitionPhone = await RequirePhoneAsync(
                cucm, dnPartitionPhoneName, context.CancellationToken);
            var dnPartitionLine = dnPartitionPhone.Lines.FirstOrDefault(
                candidate => candidate.Index == dnPartitionIndex);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Route partition for {dnPartitionPattern} on line {dnPartitionIndex}",
                "Route partition (leave blank for none)",
                ["phones", "set-dn", dnPartitionPhoneName, dnPartitionIndex.ToString(), dnPartitionPattern],
                dnPartitionLine?.RoutePartitionName,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["dids", var phoneNameForDids, var didIndexText] &&
            int.TryParse(didIndexText, out var didIndex) && didIndex > 0)
        {
            await RespondWithAvailableUserDidsAsync(context, phoneNameForDids, didIndex);
            return 0;
        }
        if (context.Arguments is
            ["did-review", var phoneNameForDidReview, var reviewIndexText, var reviewPattern, var reviewPartition] &&
            int.TryParse(reviewIndexText, out var reviewIndex) && reviewIndex > 0)
        {
            var phone = await RequirePhoneAsync(
                cucm,
                phoneNameForDidReview,
                context.CancellationToken);
            var line = phone.Lines.FirstOrDefault(candidate => candidate.Index == reviewIndex);
            var did = await RequireAvailableUserDidAsync(
                context,
                reviewPattern,
                reviewPartition);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                did.RoutePartitionName,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            var reviewResolvedPartition = existing?.RoutePartitionName ?? did.RoutePartitionName;
            await context.RespondAsync(new ModuleTableResponse(
                "Review user DN assignment",
                ["PHONE", "SLOT", "CURRENT", "USER DN", "PARTITION", "DN ACTION", "OWNER"],
                [
                    new ModuleTableRow(
                        "assign",
                        [
                            phone.Name ?? phoneNameForDidReview,
                            reviewIndex.ToString(),
                            string.IsNullOrWhiteSpace(line?.Pattern) ? "<Empty>" : Clean(line.Pattern),
                            did.Pattern,
                            DisplayPartition(reviewResolvedPartition),
                            existing is null ? "Create in CUCM" : "Use existing CUCM DN",
                            Clean(phone.OwnerUserName),
                        ],
                        [
                            "phones", "assign-did", phoneNameForDidReview, reviewIndex.ToString(),
                            did.Pattern, did.RoutePartitionName ?? string.Empty,
                            reviewResolvedPartition ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["assign-did", var phoneNameForDidAssignment, var assignmentIndexText,
                var assignmentPattern, var inventoryPartition, var assignmentResolvedPartition] &&
            int.TryParse(assignmentIndexText, out var assignmentIndex) && assignmentIndex > 0)
        {
            var phone = await RequirePhoneAsync(
                cucm,
                phoneNameForDidAssignment,
                context.CancellationToken);
            var did = await RequireAvailableUserDidAsync(
                context,
                assignmentPattern,
                inventoryPartition);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                Normalize(assignmentResolvedPartition),
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            if (existing is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        did.Pattern,
                        Normalize(assignmentResolvedPartition),
                        did.Description,
                        did.CallingSearchSpaceName,
                        did.VoiceMailProfileName),
                    context.CancellationToken);
            }
            var lineExistedBeforeAssignment = phone.Lines.Any(line => line.Index == assignmentIndex);
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberAsync(
                    phoneNameForDidAssignment,
                    assignmentIndex,
                    did.Pattern,
                    Normalize(assignmentResolvedPartition),
                    context.CancellationToken);
            }
            catch (Exception assignException) when (!lineExistedBeforeAssignment)
            {
                throw new InvalidOperationException(
                    $"Could not add a new line at index {assignmentIndex} on '{phoneNameForDidAssignment}'. " +
                    $"This phone's button template ('{Clean(phone.PhoneTemplateName)}') may not define a " +
                    "Line-type button at that position. Change the phone's button template via " +
                    $"'phones edit' to one with more line positions, then retry. CUCM error: " +
                    $"{assignException.Message}",
                    assignException);
            }
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern,
                did.RoutePartitionName,
                phoneNameForDidAssignment,
                assignmentIndex,
                phone.OwnerUserName,
                Normalize(assignmentResolvedPartition),
                context.CancellationToken);
            var didComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, phoneNameForDidAssignment, null, context.CancellationToken);
            context.Output.WriteLine(
                $"Assigned user DN '{did.Pattern}' to line {assignmentIndex} on {phoneNameForDidAssignment}" +
                (existing is null ? " after creating the DN in CUCM." : ".") +
                $" Description: '{didComposedDescription}'.");
            return 0;
        }
        if (context.Arguments is
            ["set-dn", var phoneNameForDnUpdate, var dnUpdateIndexText, var newDn, var newDnPartitionRaw] &&
            int.TryParse(dnUpdateIndexText, out var dnUpdateIndex) && dnUpdateIndex > 0)
        {
            var dnUpdatePhone = await RequirePhoneAsync(
                cucm, phoneNameForDnUpdate, context.CancellationToken);
            var lineExistedBeforeDnUpdate = dnUpdatePhone.Lines.Any(line => line.Index == dnUpdateIndex);
            var newDnPartition = Normalize(newDnPartitionRaw);
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberAsync(
                    phoneNameForDnUpdate,
                    dnUpdateIndex,
                    newDn,
                    newDnPartition,
                    context.CancellationToken);
            }
            catch (Exception assignException) when (!lineExistedBeforeDnUpdate)
            {
                throw new InvalidOperationException(
                    $"Could not add a new line at index {dnUpdateIndex} on '{phoneNameForDnUpdate}'. " +
                    $"This phone's button template ('{Clean(dnUpdatePhone.PhoneTemplateName)}') may not " +
                    "define a Line-type button at that position. Change the phone's button template via " +
                    $"'phones edit' to one with more line positions, then retry. CUCM error: " +
                    $"{assignException.Message}",
                    assignException);
            }
            var dnUpdateComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, phoneNameForDnUpdate, null, context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {dnUpdateIndex} on {phoneNameForDnUpdate} to directory number '{newDn}'. " +
                $"Description: '{dnUpdateComposedDescription}'.");
            return 0;
        }
        if (context.Arguments is ["room-building", var phoneNameForRoomBuilding, var roomLineIndexText] &&
            int.TryParse(roomLineIndexText, out var roomBuildingLineIndex) && roomBuildingLineIndex > 0)
        {
            _ = await RequirePhoneAsync(cucm, phoneNameForRoomBuilding, context.CancellationToken);
            var buildingPatterns = RequireBuildingPatterns(context);
            await context.RespondAsync(new ModuleTableResponse(
                $"Select a building for line {roomBuildingLineIndex} on {phoneNameForRoomBuilding}",
                ["BUILDING", "PARTITION", "DEVICE POOLS"],
                buildingPatterns
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new ModuleTableRow(
                        pair.Key,
                        [
                            pair.Key,
                            pair.Value.RoutePartitionName,
                            pair.Value.DevicePoolNames.Count == 0
                                ? "<none>"
                                : string.Join(", ", pair.Value.DevicePoolNames),
                        ],
                        [
                            "phones", "room-number", phoneNameForRoomBuilding,
                            roomBuildingLineIndex.ToString(), pair.Key,
                        ]))
                    .ToArray()));
            return 0;
        }
        if (context.Arguments is
            ["room-number", var phoneNameForRoomNumber, var roomNumberLineIndexText, var roomBuildingCode] &&
            int.TryParse(roomNumberLineIndexText, out var roomNumberLineIndex) && roomNumberLineIndex > 0)
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Room number for building {roomBuildingCode}",
                "Room number (3 digits)",
                [
                    "phones", "room-review", phoneNameForRoomNumber,
                    roomNumberLineIndex.ToString(), roomBuildingCode,
                ]));
            return 0;
        }
        if (context.Arguments is
            ["room-review", var phoneNameForRoomReview, var roomReviewLineIndexText,
                var roomReviewBuildingCode, var roomNumber] &&
            int.TryParse(roomReviewLineIndexText, out var roomReviewLineIndex) && roomReviewLineIndex > 0)
        {
            if (!PhoneConfigurationChecks.IsRoomNumber(roomNumber))
            {
                throw new InvalidOperationException(
                    $"'{roomNumber}' is not a valid three-digit room number.");
            }
            var phone = await RequirePhoneAsync(cucm, phoneNameForRoomReview, context.CancellationToken);
            var line = phone.Lines.FirstOrDefault(candidate => candidate.Index == roomReviewLineIndex);
            var buildingPatterns = RequireBuildingPatterns(context);
            var buildingPattern = RequireBuildingPattern(buildingPatterns, roomReviewBuildingCode);
            var existing = await cucm.GetDirectoryNumberAsync(
                roomNumber,
                buildingPattern.RoutePartitionName,
                context.CancellationToken);
            EnsureRoomDidCanBeAssigned(roomNumber, buildingPattern.RoutePartitionName, existing);
            await context.RespondAsync(new ModuleTableResponse(
                "Review room DN assignment",
                ["PHONE", "SLOT", "CURRENT", "ROOM DN", "BUILDING", "PARTITION", "DN ACTION"],
                [
                    new ModuleTableRow(
                        "assign",
                        [
                            phone.Name ?? phoneNameForRoomReview,
                            roomReviewLineIndex.ToString(),
                            string.IsNullOrWhiteSpace(line?.Pattern) ? "<Empty>" : Clean(line.Pattern),
                            roomNumber,
                            roomReviewBuildingCode,
                            DisplayPartition(buildingPattern.RoutePartitionName),
                            existing is null ? "Create in CUCM" : "Use existing CUCM DN",
                        ],
                        [
                            "phones", "assign-room", phoneNameForRoomReview,
                            roomReviewLineIndex.ToString(), roomReviewBuildingCode, roomNumber,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["assign-room", var phoneNameForRoomAssignment, var roomAssignLineIndexText,
                var roomAssignBuildingCode, var roomAssignNumber] &&
            int.TryParse(roomAssignLineIndexText, out var roomAssignLineIndex) && roomAssignLineIndex > 0)
        {
            if (!PhoneConfigurationChecks.IsRoomNumber(roomAssignNumber))
            {
                throw new InvalidOperationException(
                    $"'{roomAssignNumber}' is not a valid three-digit room number.");
            }
            var buildingPatterns = RequireBuildingPatterns(context);
            var buildingPattern = RequireBuildingPattern(buildingPatterns, roomAssignBuildingCode);
            var existing = await cucm.GetDirectoryNumberAsync(
                roomAssignNumber,
                buildingPattern.RoutePartitionName,
                context.CancellationToken);
            EnsureRoomDidCanBeAssigned(roomAssignNumber, buildingPattern.RoutePartitionName, existing);
            if (existing is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        roomAssignNumber,
                        buildingPattern.RoutePartitionName,
                        $"Room {roomAssignNumber} ({roomAssignBuildingCode})"),
                    context.CancellationToken);
            }
            await cucm.AssignPhoneLineDirectoryNumberAsync(
                phoneNameForRoomAssignment,
                roomAssignLineIndex,
                roomAssignNumber,
                buildingPattern.RoutePartitionName,
                context.CancellationToken);
            var roomAssignComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, phoneNameForRoomAssignment, null, context.CancellationToken);
            context.Output.WriteLine(
                $"Assigned room DN '{roomAssignNumber}' (building {roomAssignBuildingCode}) to line " +
                $"{roomAssignLineIndex} on {phoneNameForRoomAssignment}" +
                (existing is null ? " after creating the DN in CUCM." : ".") +
                $" Description: '{roomAssignComposedDescription}'.");
            return 0;
        }
        if (context.Arguments is ["classroom", var classroomPhoneName])
        {
            _ = await RequirePhoneAsync(cucm, classroomPhoneName, context.CancellationToken);
            var buildingPatterns = RequireBuildingPatterns(context);
            await context.RespondAsync(new ModuleTableResponse(
                $"Select a building for {classroomPhoneName}",
                ["BUILDING", "PARTITION", "DEVICE POOLS"],
                buildingPatterns
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new ModuleTableRow(
                        pair.Key,
                        [
                            pair.Key,
                            pair.Value.RoutePartitionName,
                            pair.Value.DevicePoolNames.Count == 0
                                ? "<none>"
                                : string.Join(", ", pair.Value.DevicePoolNames),
                        ],
                        ["phones", "classroom-room", classroomPhoneName, pair.Key]))
                    .ToArray()));
            return 0;
        }
        if (context.Arguments is ["classroom-room", var classroomRoomPhoneName, var classroomBuildingCode])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Room number for building {classroomBuildingCode}",
                "Room number (3 digits)",
                ["phones", "classroom-user", classroomRoomPhoneName, classroomBuildingCode]));
            return 0;
        }
        if (context.Arguments is
            ["classroom-user", var classroomUserPhoneName, var classroomUserBuildingCode, var classroomRoomNumber])
        {
            if (!PhoneConfigurationChecks.IsRoomNumber(classroomRoomNumber))
            {
                throw new InvalidOperationException(
                    $"'{classroomRoomNumber}' is not a valid three-digit room number.");
            }
            var classroomDids = await new UserDidStore(context.DataDirectory).LoadAsync(context.CancellationToken);
            var classroomUserRows = new List<ModuleTableRow>();
            await foreach (var user in cucm.ListUsersAsync(cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(user.UserId))
                {
                    continue;
                }
                var extension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
                var status = UserDnStatus(user.UserId, extension, classroomDids);
                var did = extension is null
                    ? null
                    : classroomDids.FirstOrDefault(candidate =>
                        candidate.Pattern.Equals(extension, StringComparison.Ordinal));
                var canApply = status == "Available" || did?.Assignment is { } assignment &&
                    assignment.PhoneName.Equals(classroomUserPhoneName, StringComparison.OrdinalIgnoreCase) &&
                    assignment.LineIndex == 1 &&
                    string.Equals(assignment.UserId, user.UserId, StringComparison.OrdinalIgnoreCase);
                classroomUserRows.Add(new ModuleTableRow(
                    user.UserId,
                    [Clean(user.UserId), Clean(user.DisplayName), Clean(extension), canApply ? "Ready" : status],
                    canApply
                        ? [
                            "phones", "classroom-review", classroomUserPhoneName, classroomUserBuildingCode,
                            classroomRoomNumber, user.UserId,
                          ]
                        : null));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Select a user for {classroomUserPhoneName}",
                ["USER ID", "DISPLAY NAME", "USER DN", "STATUS"],
                classroomUserRows));
            return 0;
        }
        if (context.Arguments is
            [
                "classroom-review", var classroomReviewPhoneName, var classroomReviewBuildingCode,
                var classroomReviewRoomNumber, var classroomReviewUserId,
            ])
        {
            _ = await RequirePhoneAsync(cucm, classroomReviewPhoneName, context.CancellationToken);
            var buildingPatterns = RequireBuildingPatterns(context);
            var buildingPattern = RequireBuildingPattern(buildingPatterns, classroomReviewBuildingCode);
            var devicePoolName = RequireClassroomDevicePool(buildingPattern, classroomReviewBuildingCode);
            var phone = await RequirePhoneAsync(cucm, classroomReviewPhoneName, context.CancellationToken);
            var phoneTemplateName = RequireClassroomPhoneTemplate(
                context, buildingPattern.PhoneTemplateName ?? phone.PhoneTemplateName);
            var user = await RequireUserAsync(cucm, classroomReviewUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidForUserAsync(
                context, user, classroomReviewPhoneName, 1);
            var existingUserDn = await cucm.GetDirectoryNumberAsync(
                did.Pattern, did.RoutePartitionName, context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existingUserDn);
            var effectiveUserPartition = Normalize(existingUserDn?.RoutePartitionName) ??
                Normalize(did.RoutePartitionName);
            var existingRoomDn = await cucm.GetDirectoryNumberAsync(
                classroomReviewRoomNumber,
                buildingPattern.RoutePartitionName,
                context.CancellationToken);
            EnsureRoomDidCanBeAssigned(
                classroomReviewRoomNumber,
                buildingPattern.RoutePartitionName,
                existingRoomDn);
            await context.RespondAsync(new ModuleTableResponse(
                $"Review classroom template for {classroomReviewPhoneName}",
                ["FIELD", "VALUE"],
                [
                    new ModuleTableRow("phone", ["Phone", classroomReviewPhoneName]),
                    new ModuleTableRow("building", ["Building", classroomReviewBuildingCode]),
                    new ModuleTableRow("device-pool", ["Device pool", devicePoolName]),
                    new ModuleTableRow("phone-template", ["Phone button template", phoneTemplateName]),
                    new ModuleTableRow(
                        "room",
                        [
                            "Room DN",
                            $"{classroomReviewRoomNumber} ({DisplayPartition(buildingPattern.RoutePartitionName)})",
                        ]),
                    new ModuleTableRow(
                        "user",
                        [
                            "User",
                            $"{user.DisplayName ?? classroomReviewUserId} — {did.Pattern} " +
                                $"({DisplayPartition(effectiveUserPartition)})",
                        ]),
                    new ModuleTableRow(
                        "submit",
                        ["Apply", "Sets device pool, both lines (labels/caller ID/voicemail), and description"],
                        [
                            "phones", "classroom-apply", classroomReviewPhoneName, classroomReviewBuildingCode,
                            classroomReviewRoomNumber, classroomReviewUserId,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            [
                "classroom-apply", var classroomApplyPhoneName, var classroomApplyBuildingCode,
                var classroomApplyRoomNumber, var classroomApplyUserId,
            ])
        {
            var phone = await RequirePhoneAsync(cucm, classroomApplyPhoneName, context.CancellationToken);
            var buildingPatterns = RequireBuildingPatterns(context);
            var buildingPattern = RequireBuildingPattern(buildingPatterns, classroomApplyBuildingCode);
            var devicePoolName = RequireClassroomDevicePool(buildingPattern, classroomApplyBuildingCode);
            var roomTemplate = await RequireLineTemplateAsync(context, "classroom-room");
            var userTemplate = await RequireLineTemplateAsync(context, "classroom-user");
            if (roomTemplate.Kind != LineTemplateKind.Room || userTemplate.Kind != LineTemplateKind.User)
            {
                throw new InvalidOperationException(
                    "The classroom flow requires a room-kind 'classroom-room' template and a " +
                    "user-kind 'classroom-user' template.");
            }
            var phoneTemplateName = RequireClassroomPhoneTemplate(
                context, buildingPattern.PhoneTemplateName ?? phone.PhoneTemplateName);
            var user = await RequireUserAsync(cucm, classroomApplyUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidForUserAsync(
                context, user, classroomApplyPhoneName, 1);
            var userExistingDn = await cucm.GetDirectoryNumberAsync(
                did.Pattern, did.RoutePartitionName, context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, userExistingDn);
            var effectiveUserPartition = Normalize(userExistingDn?.RoutePartitionName) ??
                Normalize(did.RoutePartitionName);
            var roomExistingDn = await cucm.GetDirectoryNumberAsync(
                classroomApplyRoomNumber,
                buildingPattern.RoutePartitionName,
                context.CancellationToken);
            EnsureRoomDidCanBeAssigned(
                classroomApplyRoomNumber,
                buildingPattern.RoutePartitionName,
                roomExistingDn);

            if (!string.Equals(phone.DevicePoolName, devicePoolName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(phone.PhoneTemplateName, phoneTemplateName, StringComparison.OrdinalIgnoreCase))
            {
                await cucm.UpdatePhoneAsync(
                    classroomApplyPhoneName, null, devicePoolName, null, context.CancellationToken,
                    phoneTemplateName);
            }

            // School convention: line 1 carries the assigned user's DN, line 3 the shared room DN.
            await ApplyClassroomLineAsync(
                context, cucm, roomTemplate, phone, classroomApplyPhoneName, 3,
                classroomApplyRoomNumber, buildingPattern.RoutePartitionName, devicePoolName,
                room: classroomApplyRoomNumber, building: classroomApplyBuildingCode,
                ownerUserId: null, userDisplayName: null);

            var previousOwnerUserId = Normalize(phone.OwnerUserName);
            var previousDevices = user.AssociatedDevices.ToArray();
            var addAssociation = !previousDevices.Contains(
                classroomApplyPhoneName, StringComparer.OrdinalIgnoreCase);
            if (userExistingDn is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        did.Pattern, effectiveUserPartition, did.Description,
                        did.CallingSearchSpaceName, did.VoiceMailProfileName),
                    context.CancellationToken);
            }
            if (addAssociation)
            {
                await cucm.UpdateUserAssociatedDevicesAsync(
                    classroomApplyUserId, previousDevices.Append(classroomApplyPhoneName), context.CancellationToken);
            }
            await cucm.AssignPhoneLineDirectoryNumberToUserAsync(
                classroomApplyPhoneName, 1, did.Pattern, effectiveUserPartition, classroomApplyUserId,
                context.CancellationToken);
            var classroomWarnings = new List<string>();
            if (previousOwnerUserId is not null &&
                !previousOwnerUserId.Equals(classroomApplyUserId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await RemovePhoneFromPreviousOwnerAsync(
                        cucm, previousOwnerUserId, classroomApplyPhoneName, context.CancellationToken);
                }
                catch (Exception cleanupException)
                {
                    classroomWarnings.Add(
                        $"could not remove '{classroomApplyPhoneName}' from previous owner " +
                        $"'{previousOwnerUserId}' associated devices: {cleanupException.Message}");
                }
            }
            await ApplyClassroomLineAsync(
                context, cucm, userTemplate, phone, classroomApplyPhoneName, 1,
                did.Pattern, effectiveUserPartition, devicePoolName,
                room: null, building: null,
                ownerUserId: classroomApplyUserId, userDisplayName: user.DisplayName);

            var classroomComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, classroomApplyPhoneName, null, context.CancellationToken);
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern, did.RoutePartitionName, classroomApplyPhoneName, 1, classroomApplyUserId,
                effectiveUserPartition, context.CancellationToken);
            context.Output.WriteLine(
                $"Applied classroom template to {classroomApplyPhoneName}: device pool '{devicePoolName}', " +
                $"room DN '{classroomApplyRoomNumber}', user '{classroomApplyUserId}' ({did.Pattern}). " +
                $"Description: '{classroomComposedDescription}'." +
                (classroomWarnings.Count == 0
                    ? string.Empty
                    : " WARNING: " + string.Join(" ", classroomWarnings)));
            return 0;
        }
        if (context.Arguments is
            ["remove-line-review", var phoneNameForRemoveReview, var removeReviewIndexText] &&
            int.TryParse(removeReviewIndexText, out var removeReviewIndex) && removeReviewIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm, phoneNameForRemoveReview, removeReviewIndex, context.CancellationToken);
            if (string.IsNullOrWhiteSpace(line.Pattern))
            {
                throw new InvalidOperationException(
                    $"Line {removeReviewIndex} on {phoneNameForRemoveReview} has no directory number to remove.");
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Review directory number removal",
                ["PHONE", "SLOT", "NUMBER", "PARTITION", "ACTION"],
                [
                    new ModuleTableRow(
                        "remove",
                        [
                            phoneNameForRemoveReview,
                            removeReviewIndex.ToString(),
                            line.Pattern,
                            DisplayPartition(line.RoutePartitionName),
                            "Remove from phone",
                        ],
                        ["phones", "remove-line", phoneNameForRemoveReview, removeReviewIndex.ToString()]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["remove-line", var phoneNameForRemove, var removeIndexText] &&
            int.TryParse(removeIndexText, out var removeIndex) && removeIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm, phoneNameForRemove, removeIndex, context.CancellationToken);
            var removedPattern = line.Pattern;
            await cucm.RemovePhoneLineAsync(phoneNameForRemove, removeIndex, context.CancellationToken);
            var clearedLocalRecord = await new UserDidStore(context.DataDirectory).ClearAssignmentAsync(
                phoneNameForRemove,
                removeIndex,
                context.CancellationToken);
            var removeComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, phoneNameForRemove, null, context.CancellationToken);
            context.Output.WriteLine(
                $"Removed directory number '{removedPattern}' from line {removeIndex} on {phoneNameForRemove}." +
                (clearedLocalRecord
                    ? " Cleared the matching local user DN inventory assignment."
                    : string.Empty) +
                $" Description: '{removeComposedDescription}'.");
            return 0;
        }
        if (context.Arguments is ["apply-template", var phoneNameForTemplateList, var templateListIndexText] &&
            int.TryParse(templateListIndexText, out var templateListIndex) && templateListIndex > 0)
        {
            _ = await RequirePhoneAsync(cucm, phoneNameForTemplateList, context.CancellationToken);
            var templates = await LoadLineTemplatesAsync(context);
            if (templates.Count == 0)
            {
                throw new InvalidOperationException(
                    "No line templates are defined. Create one via 'vt cucm templates add' before " +
                    "applying one.");
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Apply template to line {templateListIndex} on {phoneNameForTemplateList}",
                ["TEMPLATE", "KIND", "ASSOCIATES OWNER"],
                templates
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new ModuleTableRow(
                        pair.Key,
                        [
                            pair.Key,
                            pair.Value.Kind == LineTemplateKind.Room ? "Room" : "User",
                            pair.Value.Kind == LineTemplateKind.Room
                                ? "No (room lines never associate an owner)"
                                : (pair.Value.AssociateEndUser ? "Yes" : "No"),
                        ],
                        [
                            "phones", "apply-template-context", phoneNameForTemplateList,
                            templateListIndex.ToString(), pair.Key,
                        ]))
                    .ToArray()));
            return 0;
        }
        if (context.Arguments is
            ["apply-template-context", var contextPhoneName, var contextIndexText, var contextTemplateName] &&
            int.TryParse(contextIndexText, out var contextIndex) && contextIndex > 0)
        {
            var phone = await RequirePhoneAsync(cucm, contextPhoneName, context.CancellationToken);
            var template = await RequireLineTemplateAsync(context, contextTemplateName);
            if (template.Kind == LineTemplateKind.Room)
            {
                var buildingPatterns = RequireBuildingPatterns(context);
                var resolvedBuilding = PhoneConfigurationChecks.FindBuildingCodeForPhoneButtonTemplate(
                    buildingPatterns, phone.PhoneTemplateName);
                if (resolvedBuilding is not null)
                {
                    await context.RespondAsync(new ModuleTextPromptResponse(
                        $"Room number for building {resolvedBuilding} (from button template " +
                            $"'{phone.PhoneTemplateName}')",
                        "Room number (3 digits)",
                        [
                            "phones", "apply-template-room-review", contextPhoneName,
                            contextIndex.ToString(), contextTemplateName, resolvedBuilding,
                        ]));
                    return 0;
                }
                await context.RespondAsync(new ModuleTableResponse(
                    $"Could not determine a building from button template " +
                        $"'{Clean(phone.PhoneTemplateName)}' — select one",
                    ["BUILDING", "PARTITION", "DEVICE POOLS"],
                    buildingPatterns
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => new ModuleTableRow(
                            pair.Key,
                            [
                                pair.Key,
                                pair.Value.RoutePartitionName,
                                pair.Value.DevicePoolNames.Count == 0
                                    ? "<none>"
                                    : string.Join(", ", pair.Value.DevicePoolNames),
                            ],
                            [
                                "phones", "apply-template-room-number", contextPhoneName,
                                contextIndex.ToString(), contextTemplateName, pair.Key,
                            ]))
                        .ToArray()));
                return 0;
            }
            if (!string.IsNullOrWhiteSpace(template.Pattern))
            {
                if (!string.IsNullOrWhiteSpace(template.RoutePartitionName))
                {
                    await RespondWithLineTemplateOwnerOrReviewAsync(
                        context, cucm, template, contextTemplateName, contextPhoneName, contextIndex,
                        template.Pattern, template.RoutePartitionName);
                    return 0;
                }
                await context.RespondAsync(new ModuleTextPromptResponse(
                    $"Route partition for {template.Pattern}",
                    "Route partition (leave blank for none)",
                    [
                        "phones", "apply-template-user-partition-review", contextPhoneName,
                        contextIndex.ToString(), contextTemplateName, template.Pattern,
                    ],
                    AllowEmpty: true));
                return 0;
            }
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Apply template '{contextTemplateName}' to line {contextIndex} on {contextPhoneName}",
                "Directory number pattern",
                ["phones", "apply-template-user-partition", contextPhoneName, contextIndex.ToString(),
                    contextTemplateName]));
            return 0;
        }
        if (context.Arguments is
            [
                "apply-template-room-number", var roomNumberPhoneName, var roomNumberIndexText,
                var roomNumberTemplateName, var roomNumberBuildingCode,
            ] &&
            int.TryParse(roomNumberIndexText, out var roomNumberTemplateIndex) && roomNumberTemplateIndex > 0)
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Room number for building {roomNumberBuildingCode}",
                "Room number (3 digits)",
                [
                    "phones", "apply-template-room-review", roomNumberPhoneName,
                    roomNumberTemplateIndex.ToString(), roomNumberTemplateName, roomNumberBuildingCode,
                ]));
            return 0;
        }
        if (context.Arguments is
            [
                "apply-template-room-review", var roomReviewTemplatePhoneName, var roomReviewTemplateIndexText,
                var roomReviewTemplateName, var roomReviewTemplateBuildingCode, var roomReviewTemplateNumber,
            ] &&
            int.TryParse(roomReviewTemplateIndexText, out var roomReviewTemplateIndex) &&
            roomReviewTemplateIndex > 0)
        {
            if (!PhoneConfigurationChecks.IsRoomNumber(roomReviewTemplateNumber))
            {
                throw new InvalidOperationException(
                    $"'{roomReviewTemplateNumber}' is not a valid three-digit room number.");
            }
            var phone = await RequirePhoneAsync(cucm, roomReviewTemplatePhoneName, context.CancellationToken);
            var template = await RequireLineTemplateAsync(context, roomReviewTemplateName);
            var buildingPatterns = RequireBuildingPatterns(context);
            var buildingPattern = RequireBuildingPattern(buildingPatterns, roomReviewTemplateBuildingCode);
            await RespondWithLineTemplateReviewAsync(
                context,
                cucm,
                template,
                roomReviewTemplateName,
                phone,
                roomReviewTemplatePhoneName,
                roomReviewTemplateIndex,
                roomReviewTemplateNumber,
                buildingPattern.RoutePartitionName,
                ownerUserId: null,
                room: roomReviewTemplateNumber,
                building: roomReviewTemplateBuildingCode);
            return 0;
        }
        if (context.Arguments is
            [
                "apply-template-user-partition", var userPartitionPhoneName, var userPartitionIndexText,
                var userPartitionTemplateName, var userPartitionPattern,
            ] &&
            int.TryParse(userPartitionIndexText, out var userPartitionIndex) && userPartitionIndex > 0 &&
            !string.IsNullOrWhiteSpace(userPartitionPattern))
        {
            var template = await RequireLineTemplateAsync(context, userPartitionTemplateName);
            if (!string.IsNullOrWhiteSpace(template.RoutePartitionName))
            {
                await RespondWithLineTemplateOwnerOrReviewAsync(
                    context,
                    cucm,
                    template,
                    userPartitionTemplateName,
                    userPartitionPhoneName,
                    userPartitionIndex,
                    userPartitionPattern,
                    template.RoutePartitionName);
                return 0;
            }
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Route partition for {userPartitionPattern}",
                "Route partition (leave blank for none)",
                [
                    "phones", "apply-template-user-partition-review", userPartitionPhoneName,
                    userPartitionIndex.ToString(), userPartitionTemplateName, userPartitionPattern,
                ],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "apply-template-user-partition-review", var userPartitionReviewPhoneName,
                var userPartitionReviewIndexText, var userPartitionReviewTemplateName,
                var userPartitionReviewPattern, var userPartitionReviewPartitionRaw,
            ] &&
            int.TryParse(userPartitionReviewIndexText, out var userPartitionReviewIndex) &&
            userPartitionReviewIndex > 0)
        {
            var template = await RequireLineTemplateAsync(context, userPartitionReviewTemplateName);
            await RespondWithLineTemplateOwnerOrReviewAsync(
                context,
                cucm,
                template,
                userPartitionReviewTemplateName,
                userPartitionReviewPhoneName,
                userPartitionReviewIndex,
                userPartitionReviewPattern,
                Normalize(userPartitionReviewPartitionRaw));
            return 0;
        }
        if (context.Arguments is
            [
                "apply-template-user-owner-review", var userOwnerReviewPhoneName, var userOwnerReviewIndexText,
                var userOwnerReviewTemplateName, var userOwnerReviewPattern, var userOwnerReviewPartitionRaw,
                var userOwnerReviewOwnerRaw,
            ] &&
            int.TryParse(userOwnerReviewIndexText, out var userOwnerReviewIndex) && userOwnerReviewIndex > 0)
        {
            var phone = await RequirePhoneAsync(cucm, userOwnerReviewPhoneName, context.CancellationToken);
            var template = await RequireLineTemplateAsync(context, userOwnerReviewTemplateName);
            await RespondWithLineTemplateReviewAsync(
                context,
                cucm,
                template,
                userOwnerReviewTemplateName,
                phone,
                userOwnerReviewPhoneName,
                userOwnerReviewIndex,
                userOwnerReviewPattern,
                Normalize(userOwnerReviewPartitionRaw),
                ownerUserId: Normalize(userOwnerReviewOwnerRaw),
                room: null,
                building: null);
            return 0;
        }
        if (context.Arguments is
            [
                "apply-template-apply", var applyPhoneName, var applyIndexText, var applyPattern,
                var applyPartitionRaw, var applyAlertingNameRaw, var applyDisplayRaw, var applyLabelRaw,
                var applyExternalMaskRaw, var applyVoiceMailProfileRaw, var applyOwnerUserIdRaw,
            ] &&
            int.TryParse(applyIndexText, out var applyIndex) && applyIndex > 0)
        {
            var phone = await RequirePhoneAsync(cucm, applyPhoneName, context.CancellationToken);
            var applyPartition = Normalize(applyPartitionRaw);
            var applyAlertingName = Normalize(applyAlertingNameRaw);
            var applyDisplay = Normalize(applyDisplayRaw);
            var applyLabel = Normalize(applyLabelRaw);
            var applyExternalMask = Normalize(applyExternalMaskRaw);
            var applyVoiceMailProfile = Normalize(applyVoiceMailProfileRaw);
            var applyOwnerUserId = Normalize(applyOwnerUserIdRaw);

            var existing = await cucm.GetDirectoryNumberAsync(
                applyPattern, applyPartition, context.CancellationToken);
            if (existing is not null &&
                !string.Equals(
                    Normalize(existing.RoutePartitionName),
                    applyPartition,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"DN '{applyPattern}' already exists in partition " +
                    $"'{DisplayPartition(existing.RoutePartitionName)}', not the expected " +
                    $"'{DisplayPartition(applyPartition)}'. Resolve the conflict in CUCM before applying " +
                    "this template.");
            }
            if (existing is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        applyPattern,
                        applyPartition,
                        applyAlertingName ?? applyLabel ?? $"{applyPattern} ({DisplayPartition(applyPartition)})",
                        VoiceMailProfileName: applyVoiceMailProfile),
                    context.CancellationToken);
            }

            var lineExistedBeforeApply = phone.Lines.Any(line => line.Index == applyIndex);
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberAsync(
                    applyPhoneName, applyIndex, applyPattern, applyPartition, context.CancellationToken);
            }
            catch (Exception assignException) when (!lineExistedBeforeApply)
            {
                throw new InvalidOperationException(
                    $"Could not add a new line at index {applyIndex} on '{applyPhoneName}'. This " +
                    $"phone's button template ('{Clean(phone.PhoneTemplateName)}') may not define a " +
                    "Line-type button at that position. Change the phone's button template via " +
                    $"'phones edit' to one with more line positions, then retry. CUCM error: " +
                    $"{assignException.Message}",
                    assignException);
            }
            if (applyAlertingName is not null || applyVoiceMailProfile is not null)
            {
                await cucm.UpdateDirectoryNumberAsync(
                    new CucmDirectoryNumberUpdateRequest(
                        applyPattern,
                        applyPartition,
                        AlertingName: applyAlertingName,
                        VoiceMailProfileName: applyVoiceMailProfile),
                    context.CancellationToken);
            }
            if (applyDisplay is not null)
            {
                await cucm.UpdatePhoneLineDisplayAsync(
                    applyPhoneName, applyIndex, applyDisplay, applyDisplay, context.CancellationToken);
            }
            if (applyLabel is not null)
            {
                await cucm.UpdatePhoneLineLabelAsync(
                    applyPhoneName, applyIndex, applyLabel, context.CancellationToken);
            }
            if (applyExternalMask is not null)
            {
                await cucm.UpdatePhoneLineExternalMaskAsync(
                    applyPhoneName, applyIndex, applyExternalMask, context.CancellationToken);
            }
            if (applyOwnerUserId is not null)
            {
                await cucm.UpdatePhoneAsync(
                    applyPhoneName, null, null, applyOwnerUserId, context.CancellationToken);
                if (!string.IsNullOrWhiteSpace(phone.OwnerUserName) &&
                    !phone.OwnerUserName.Equals(applyOwnerUserId, StringComparison.OrdinalIgnoreCase))
                {
                    await RemovePhoneFromPreviousOwnerAsync(
                        cucm, phone.OwnerUserName, applyPhoneName, context.CancellationToken);
                }
            }
            var applyComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, applyPhoneName, null, context.CancellationToken);
            context.Output.WriteLine(
                $"Applied template to line {applyIndex} on {applyPhoneName}: DN '{applyPattern}' " +
                $"({DisplayPartition(applyPartition)})" +
                (existing is null ? " (created in CUCM)" : string.Empty) +
                (applyOwnerUserId is null ? string.Empty : $", owner '{applyOwnerUserId}'") +
                $". Description: '{applyComposedDescription}'.");
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
        if (context.Arguments is ["caller-id", var phoneNameForCallerIdPrompt, var callerIdIndexText] &&
            int.TryParse(callerIdIndexText, out var callerIdIndex) && callerIdIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForCallerIdPrompt,
                callerIdIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set caller ID for {line.Pattern ?? $"line {callerIdIndex}"}",
                "Caller ID (displayed on outbound calls)",
                ["phones", "set-caller-id", phoneNameForCallerIdPrompt, callerIdIndex.ToString()],
                line.Display,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            ["set-caller-id", var phoneNameForCallerIdUpdate, var callerIdUpdateIndexText, var newCallerId] &&
            int.TryParse(callerIdUpdateIndexText, out var callerIdUpdateIndex) && callerIdUpdateIndex > 0)
        {
            await cucm.UpdatePhoneLineDisplayAsync(
                phoneNameForCallerIdUpdate,
                callerIdUpdateIndex,
                newCallerId,
                newCallerId,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {callerIdUpdateIndex} on {phoneNameForCallerIdUpdate} with caller ID " +
                $"'{newCallerId}'.");
            return 0;
        }
        if (context.Arguments is
            ["alerting-name", var phoneNameForAlertingPrompt, var alertingIndexText] &&
            int.TryParse(alertingIndexText, out var alertingIndex) && alertingIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm, phoneNameForAlertingPrompt, alertingIndex, context.CancellationToken);
            var dn = await cucm.GetDirectoryNumberAsync(
                line.Pattern!, line.RoutePartitionName, context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set alerting name for {line.Pattern ?? $"line {alertingIndex}"}",
                "Alerting name (shown on the called party's phone)",
                ["phones", "set-alerting-name", phoneNameForAlertingPrompt, alertingIndex.ToString()],
                dn?.AlertingName,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            ["set-alerting-name", var phoneNameForAlertingUpdate, var alertingUpdateIndexText,
                var newAlertingName] &&
            int.TryParse(alertingUpdateIndexText, out var alertingUpdateIndex) && alertingUpdateIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm, phoneNameForAlertingUpdate, alertingUpdateIndex, context.CancellationToken);
            await cucm.UpdateDirectoryNumberAsync(
                new CucmDirectoryNumberUpdateRequest(
                    line.Pattern!,
                    line.RoutePartitionName,
                    AlertingName: newAlertingName),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated {line.Pattern} with alerting name '{newAlertingName}'.");
            return 0;
        }
        if (context.Arguments is
            ["external-mask", var phoneNameForMaskPrompt, var maskIndexText] &&
            int.TryParse(maskIndexText, out var maskIndex) && maskIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm, phoneNameForMaskPrompt, maskIndex, context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set external phone number mask for {line.Pattern ?? $"line {maskIndex}"}",
                "External phone number mask (e.g. 555XXXX)",
                ["phones", "set-external-mask", phoneNameForMaskPrompt, maskIndex.ToString()],
                line.ExternalPhoneNumberMask,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            ["set-external-mask", var phoneNameForMaskUpdate, var maskUpdateIndexText, var newMask] &&
            int.TryParse(maskUpdateIndexText, out var maskUpdateIndex) && maskUpdateIndex > 0)
        {
            await cucm.UpdatePhoneLineExternalMaskAsync(
                phoneNameForMaskUpdate,
                maskUpdateIndex,
                newMask,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {maskUpdateIndex} on {phoneNameForMaskUpdate} with external phone " +
                $"number mask '{newMask}'.");
            return 0;
        }
        if (context.Arguments is
            ["dn-owner", var phoneNameForOwnerPrompt, var ownerIndexText] &&
            int.TryParse(ownerIndexText, out var ownerIndex) && ownerIndex > 0)
        {
            var phone = await RequirePhoneAsync(cucm, phoneNameForOwnerPrompt, context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set owner for line {ownerIndex} on {phoneNameForOwnerPrompt}",
                "Owner user ID (leave blank to clear... " +
                    "note: CUCM currently leaves an existing owner unchanged if left blank)",
                ["phones", "dn-owner-review", phoneNameForOwnerPrompt, ownerIndex.ToString()],
                phone.OwnerUserName,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            ["dn-owner-review", var phoneNameForOwnerReview, var ownerReviewIndexText, var ownerReviewUserId] &&
            int.TryParse(ownerReviewIndexText, out var ownerReviewIndex) && ownerReviewIndex > 0)
        {
            var phone = await RequirePhoneAsync(cucm, phoneNameForOwnerReview, context.CancellationToken);
            var newOwnerId = Normalize(ownerReviewUserId);
            if (newOwnerId is not null)
            {
                // Validate early so a typo doesn't silently no-op the phone update below.
                await RequireUserAsync(cucm, newOwnerId, context.CancellationToken);
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Review owner change",
                ["PHONE", "LINE", "CURRENT OWNER", "NEW OWNER", "ACTION"],
                [
                    new ModuleTableRow(
                        "submit",
                        [
                            phoneNameForOwnerReview,
                            ownerReviewIndex.ToString(),
                            Clean(phone.OwnerUserName),
                            newOwnerId ?? "<none>",
                            newOwnerId is null
                                ? "Leave unchanged (CUCM cannot clear an owner via this API)"
                                : OwnerActionLabel(phone.OwnerUserName, newOwnerId),
                        ],
                        [
                            "phones", "set-dn-owner", phoneNameForOwnerReview, ownerReviewIndex.ToString(),
                            ownerReviewUserId,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["set-dn-owner", var phoneNameForOwnerUpdate, var ownerUpdateIndexText, var ownerUpdateUserIdRaw] &&
            int.TryParse(ownerUpdateIndexText, out var ownerUpdateIndex) && ownerUpdateIndex > 0)
        {
            var phone = await RequirePhoneAsync(cucm, phoneNameForOwnerUpdate, context.CancellationToken);
            var newOwnerId = Normalize(ownerUpdateUserIdRaw);
            if (newOwnerId is not null)
            {
                await cucm.UpdatePhoneAsync(
                    phoneNameForOwnerUpdate, null, null, newOwnerId, context.CancellationToken);
                if (!string.IsNullOrWhiteSpace(phone.OwnerUserName) &&
                    !phone.OwnerUserName.Equals(newOwnerId, StringComparison.OrdinalIgnoreCase))
                {
                    await RemovePhoneFromPreviousOwnerAsync(
                        cucm, phone.OwnerUserName, phoneNameForOwnerUpdate, context.CancellationToken);
                }
            }
            var ownerComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, phoneNameForOwnerUpdate, null, context.CancellationToken);
            context.Output.WriteLine(
                (newOwnerId is null
                    ? $"Left the owner of {phoneNameForOwnerUpdate} unchanged."
                    : $"Set the owner of {phoneNameForOwnerUpdate} to '{newOwnerId}'.") +
                $" Description: '{ownerComposedDescription}'.");
            return 0;
        }
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add CUCM Phone", 
                "MAC address", 
                ["phones", "add-mac"])); 
            return 0;
        }
        if (context.Arguments is ["add-mac", var macRaw] && MACAddress.TryParse(macRaw, out var mac))
        {
            var macPhoneName = $"SEP{mac!.Value}";
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {macPhoneName}",
                "Description",
                ["phones", "add-product", macPhoneName],
                AllowEmpty: true));
            return 0;

        }
        if (context.Arguments is ["add-description", var addName] &&
            !string.IsNullOrWhiteSpace(addName))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {addName}",
                "Description",
                ["phones", "add-product", addName],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["add-product", var productPhoneName, var productDescription])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {productPhoneName}",
                "Product (e.g. \"Cisco 8841\")",
                ["phones", "add-device-pool", productPhoneName, productDescription]));
            return 0;
        }
        if (context.Arguments is
            ["add-device-pool", var devicePoolPhoneName, var devicePoolDescription, var devicePoolProduct] &&
            !string.IsNullOrWhiteSpace(devicePoolProduct))
        {
            await RespondWithPhoneNamedSelectorAsync(
                context,
                cucm,
                "Select device pool",
                "add-template",
                new PhoneWizardState(
                    devicePoolPhoneName,
                    Normalize(devicePoolDescription),
                    devicePoolProduct),
                "device-pool",
                allowNone: false);
            return 0;
        }
        if (TryParsePhoneWizardState(context.Arguments, "add-template", out var templateState))
        {
            await RespondWithPhoneNamedSelectorAsync(
                context,
                cucm,
                "Select phone button template",
                "add-security-profile",
                templateState,
                "phone-template",
                allowNone: true);
            return 0;
        }
        if (TryParsePhoneWizardState(
            context.Arguments,
            "add-security-profile",
            out var securityProfileState))
        {
            await RespondWithPhoneNamedSelectorAsync(
                context,
                cucm,
                "Select security profile",
                "add-owner",
                securityProfileState,
                "security-profile",
                allowNone: true);
            return 0;
        }
        if (TryParsePhoneWizardState(context.Arguments, "add-owner", out var ownerState))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {ownerState.Name}",
                "Owner user ID",
                PhoneWizardArguments("add-review-value", ownerState),
                AllowEmpty: true));
            return 0;
        }
        if (TryParsePhoneWizardStateWithValue(
            context.Arguments,
            "add-review-value",
            out var reviewValueState,
            out var ownerValue))
        {
            await RespondWithPhoneCreateReviewAsync(
                context,
                reviewValueState with { OwnerUserName = Normalize(ownerValue) });
            return 0;
        }
        if (TryParsePhoneWizardState(context.Arguments, "add-create", out var createState))
        {
            var uuid = await cucm.AddPhoneAsync(
                createState.ToCreateRequest(),
                context.CancellationToken);
            var addComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, createState.Name, createState.Description, context.CancellationToken);
            context.Output.WriteLine(
                $"Created phone '{createState.Name}'" +
                (string.IsNullOrWhiteSpace(uuid) ? "." : $" ({uuid}).") +
                $" Description: '{addComposedDescription}'." +
                $" Use 'phones select {createState.Name}' -> Numbers to assign its lines/DNs.");
            return 0;
        }
        if (context.Arguments is ["edit", var editPhoneName])
        {
            var phone = await RequirePhoneAsync(cucm, editPhoneName, context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Edit phone {editPhoneName}",
                "Description (fallback name/text; used verbatim when compliance can't be evaluated, " +
                    "or in place of the owner's name when no owner is assigned)",
                ["phones", "edit-device-pool", editPhoneName],
                PhoneConfigurationChecks.ExtractRawDescription(phone.Description),
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            ["edit-device-pool", var devicePoolEditPhoneName, var editDescriptionRaw])
        {
            var editDescription = Normalize(editDescriptionRaw);
            var rows = new List<ModuleTableRow>
            {
                new(
                    "keep",
                    ["<Keep current>", string.Empty],
                    [
                        "phones", "edit-owner", devicePoolEditPhoneName,
                        editDescription ?? string.Empty, string.Empty,
                    ]),
            };
            await foreach (var pool in cucm.ListDevicePoolsAsync(
                cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(pool.Name))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    pool.Uuid ?? $"device-pool:{pool.Name}",
                    [Clean(pool.Name), Clean(pool.Description)],
                    [
                        "phones", "edit-owner", devicePoolEditPhoneName,
                        editDescription ?? string.Empty, pool.Name,
                    ]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Select device pool",
                ["NAME", "DESCRIPTION"],
                rows,
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["edit-owner", var ownerEditPhoneName, var ownerEditDescriptionRaw, var ownerEditDevicePoolRaw])
        {
            var phone = await RequirePhoneAsync(
                cucm,
                ownerEditPhoneName,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Edit phone {ownerEditPhoneName}",
                "Owner user ID",
                [
                    "phones", "edit-template", ownerEditPhoneName,
                    ownerEditDescriptionRaw, ownerEditDevicePoolRaw,
                ],
                phone.OwnerUserName,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "edit-template", var templateEditPhoneName, var templateEditDescriptionRaw,
                var templateEditDevicePoolRaw, var templateEditOwnerRaw,
            ])
        {
            var rows = new List<ModuleTableRow>
            {
                new(
                    "keep",
                    ["<Keep current>", string.Empty],
                    [
                        "phones", "edit-review", templateEditPhoneName,
                        templateEditDescriptionRaw, templateEditDevicePoolRaw, templateEditOwnerRaw,
                        string.Empty,
                    ]),
            };
            await foreach (var template in cucm.ListPhoneButtonTemplatesAsync(
                cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(template.Name))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    template.Uuid ?? $"phone-button-template:{template.Name}",
                    [Clean(template.Name), Clean(template.Description)],
                    [
                        "phones", "edit-review", templateEditPhoneName,
                        templateEditDescriptionRaw, templateEditDevicePoolRaw, templateEditOwnerRaw,
                        template.Name,
                    ]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Select phone button template",
                ["NAME", "DESCRIPTION"],
                rows,
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            [
                "edit-review", var reviewPhoneName, var reviewDescriptionRaw, var reviewDevicePoolRaw,
                var reviewOwnerRaw, var reviewTemplateRaw,
            ])
        {
            // Empty values mean "leave unchanged" (UpdatePhoneAsync omits null fields), so
            // reviewing an intentional blank-out isn't distinguishable from "no change" here.
            await context.RespondAsync(new ModuleTableResponse(
                $"Review changes to {reviewPhoneName}",
                ["PHONE", "DESCRIPTION", "DEVICE POOL", "OWNER", "BUTTON TEMPLATE"],
                [
                    new ModuleTableRow(
                        "submit",
                        [
                            reviewPhoneName,
                            string.IsNullOrWhiteSpace(reviewDescriptionRaw)
                                ? "<Unchanged>"
                                : Clean(reviewDescriptionRaw),
                            string.IsNullOrWhiteSpace(reviewDevicePoolRaw)
                                ? "<Unchanged>"
                                : Clean(reviewDevicePoolRaw),
                            string.IsNullOrWhiteSpace(reviewOwnerRaw)
                                ? "<Unchanged>"
                                : Clean(reviewOwnerRaw),
                            string.IsNullOrWhiteSpace(reviewTemplateRaw)
                                ? "<Unchanged>"
                                : Clean(reviewTemplateRaw),
                        ],
                        [
                            "phones", "edit-update", reviewPhoneName,
                            reviewDescriptionRaw, reviewDevicePoolRaw, reviewOwnerRaw, reviewTemplateRaw,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            [
                "edit-update", var updatePhoneName, var updateDescriptionRaw, var updateDevicePoolRaw,
                var updateOwnerRaw, var updateTemplateRaw,
            ])
        {
            // Description is intentionally omitted here: it's recomposed and saved below by
            // ComposeAndApplyPhoneDescriptionAsync, after the device pool/owner/template changes
            // above have already landed in CUCM (so compliance evaluates against the new values).
            await cucm.UpdatePhoneAsync(
                updatePhoneName,
                null,
                Normalize(updateDevicePoolRaw),
                Normalize(updateOwnerRaw),
                context.CancellationToken,
                Normalize(updateTemplateRaw));
            var updateComposedDescription = await ComposeAndApplyPhoneDescriptionAsync(
                context, cucm, updatePhoneName, Normalize(updateDescriptionRaw), context.CancellationToken);
            context.Output.WriteLine(
                $"Updated phone '{updatePhoneName}'. Description: '{updateComposedDescription}'.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm phones list [--max <count>] [--page-size <count>]\n" +
            "       vt cucm phones add\n" +
            "       vt cucm phones check <phone> [basic-room|classroom|room-routing]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task RespondWithAvailableUserDidsAsync(
    ModuleContext context,
    string phoneName,
    int lineIndex)
{
    var available = (await new UserDidStore(context.DataDirectory)
        .LoadAsync(context.CancellationToken))
        .Where(did => did.Assignment is null)
        .ToArray();
    await context.RespondAsync(new ModuleTableResponse(
        $"Available user DNs for line {lineIndex} on {phoneName}",
        ["DID", "PARTITION", "DESCRIPTION", "STATUS"],
        available.Select(did => new ModuleTableRow(
            $"{did.Pattern}:{did.RoutePartitionName}",
            [
                did.Pattern,
                DisplayPartition(did.RoutePartitionName),
                Clean(did.Description),
                string.IsNullOrWhiteSpace(did.RoutePartitionName)
                    ? "Resolve from CUCM"
                    : "Ready",
            ],
            [
                "phones", "did-review", phoneName, lineIndex.ToString(),
                did.Pattern, did.RoutePartitionName ?? string.Empty,
            ])).ToArray()));
}

static async ValueTask<int> UserDidsAsync(ModuleContext context)
{
    try
    {
        var store = new UserDidStore(context.DataDirectory);
        if (context.Arguments.Count == 0)
        {
            await context.RespondAsync(new ModuleTableResponse(
                "User DN inventory",
                ["ACTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "list",
                        ["List", "Browse available and assigned user DNs"],
                        ["dids", "list"]),
                    new ModuleTableRow(
                        "add",
                        ["Add", "Import comma-separated four-digit DNs or a numeric start..end range"],
                        ["dids", "add"]),
                    new ModuleTableRow(
                        "replace",
                        ["Replace", "Replace the unassigned inventory after Save confirmation"],
                        ["dids", "replace"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["list"])
        {
            var dids = await store.LoadAsync(context.CancellationToken);
            await context.RespondAsync(new ModuleTableResponse(
                "Local user DN inventory",
                ["DID", "PARTITION", "STATUS", "USER", "PHONE", "SLOT"],
                dids.Select(did => new ModuleTableRow(
                    $"{did.Pattern}:{did.RoutePartitionName}",
                    [
                        did.Pattern,
                        DisplayPartition(
                            did.Assignment?.RoutePartitionName ?? did.RoutePartitionName),
                        did.Assignment is null ? "Available" : "Assigned",
                        Clean(did.Assignment?.UserId),
                        Clean(did.Assignment?.PhoneName),
                        did.Assignment?.LineIndex.ToString() ?? string.Empty,
                    ])).ToArray(),
                Selectable: false));
            return 0;
        }
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add user DNs",
                "Comma-separated four-digit DNs or numeric start..end range",
                ["dids", "add-values"]));
            return 0;
        }
        if (context.Arguments is ["replace"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Replace user DNs",
                "Comma-separated four-digit extensions or numeric start..end range",
                ["dids", "replace-review"]));
            return 0;
        }
        if (context.Arguments is ["replace-review", var replacementValues])
        {
            var replacementPatterns = UserDidStore.ParsePatterns(replacementValues);
            await context.RespondAsync(new ModuleTableResponse(
                "Review user DN inventory replacement",
                ["ACTION", "NEW COUNT", "FIRST", "LAST"],
                [
                    new ModuleTableRow(
                        "replace",
                        [
                            "Replace inventory",
                            replacementPatterns.Count.ToString(),
                            replacementPatterns.Order(StringComparer.Ordinal).First(),
                            replacementPatterns.Order(StringComparer.Ordinal).Last(),
                        ],
                        ["dids", "replace-values", replacementValues]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["replace-values", var confirmedReplacementValues])
        {
            var replacementPatterns = UserDidStore.ParsePatterns(confirmedReplacementValues);
            await store.ReplaceAsync(
                replacementPatterns,
                context.Configuration.GetValueOrDefault("user-did-partition"),
                context.Configuration.GetValueOrDefault("user-did-description-prefix"),
                context.Configuration.GetValueOrDefault("user-did-css"),
                context.Configuration.GetValueOrDefault("user-did-voicemail-profile"),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Replaced the local inventory with {replacementPatterns.Count} user DNs.");
            return 0;
        }
        if (context.Arguments.Count == 2 &&
            context.Arguments[0] is "add-values" or "add")
        {
            var values = context.Arguments[1];
            var patterns = UserDidStore.ParsePatterns(values);
            var added = await store.AddAsync(
                patterns,
                context.Configuration.GetValueOrDefault("user-did-partition"),
                context.Configuration.GetValueOrDefault("user-did-description-prefix"),
                context.Configuration.GetValueOrDefault("user-did-css"),
                context.Configuration.GetValueOrDefault("user-did-voicemail-profile"),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Added {added} user DN{(added == 1 ? string.Empty : "s")}; " +
                $"{patterns.Count - added} already existed in the local inventory.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm dids [list|add [<dn,dn|start..end>]|replace]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<UserDid> RequireAvailableUserDidAsync(
    ModuleContext context,
    string pattern,
    string routePartitionName)
{
    var partition = Normalize(routePartitionName);
    var did = (await new UserDidStore(context.DataDirectory).LoadAsync(context.CancellationToken))
        .FirstOrDefault(candidate =>
            candidate.Pattern.Equals(pattern, StringComparison.Ordinal) &&
            string.Equals(candidate.RoutePartitionName, partition, StringComparison.Ordinal));
    return did is null
        ? throw new InvalidOperationException(
            $"User DN '{pattern}' is not present in the local inventory.")
        : did.Assignment is not null
            ? throw new InvalidOperationException($"User DN '{pattern}' is no longer available.")
            : did;
}

static void EnsureUserDidCanBeAssigned(UserDid did, CucmDirectoryNumber? existing)
{
    if (existing is null && string.IsNullOrWhiteSpace(did.RoutePartitionName))
    {
        throw new InvalidOperationException(
            $"User DN '{did.Pattern}' does not exist in CUCM and has no route partition. " +
            "Configure its location-specific defaults before creating it.");
    }
}

static IReadOnlyDictionary<string, BuildingPattern> RequireBuildingPatterns(ModuleContext context)
{
    var buildingPatterns = PhoneConfigurationChecks.ParseBuildingPatterns(
        context.Configuration.GetValueOrDefault("building-patterns") ?? "{}");
    if (buildingPatterns.Count == 0)
    {
        throw new InvalidOperationException(
            "CUCM setting 'building-patterns' has no buildings configured. Define at least one " +
            "building with a 'routePartitionName' before creating room DNs.");
    }
    return buildingPatterns;
}

// Recomputes and (if changed) saves a phone's CUCM Description as a compliance summary, derived from
// its assigned phone button template's compliance policy (see 'template-compliance-policies'):
//   Compliant/non-compliant: "{check|cross} | {RoomNumber} | {owner display name or fallback text}"
//   Cannot be evaluated:     "? | {fallback text}"
// manualDescriptionRaw, when provided, is an explicit override for the fallback text (e.g. freshly
// typed into an edit/provision "Description" prompt) rather than re-extracting it from the phone's
// current CUCM description. Call this after any change that could affect the formula's inputs
// (owner, device pool, phone button template, or a line matching a policy slot).
static async Task<string> ComposeAndApplyPhoneDescriptionAsync(
    ModuleContext context,
    CucmService cucm,
    string phoneName,
    string? manualDescriptionRaw,
    CancellationToken cancellationToken)
{
    var phone = await RequirePhoneAsync(cucm, phoneName, cancellationToken);
    var rawText = manualDescriptionRaw ?? PhoneConfigurationChecks.ExtractRawDescription(phone.Description);
    var buildingPatterns = PhoneConfigurationChecks.ParseBuildingPatterns(
        context.Configuration.GetValueOrDefault("building-patterns") ?? "{}");
    var policies = PhoneConfigurationChecks.ParseTemplateCompliancePolicies(
        context.Configuration.GetValueOrDefault("template-compliance-policies") ?? "{}");
    var compliance = PhoneConfigurationChecks.EvaluateTemplateCompliance(phone, policies, buildingPatterns);

    var userOrDescription = rawText;
    if (!string.IsNullOrWhiteSpace(phone.OwnerUserName))
    {
        var owner = await cucm.GetUserAsync(phone.OwnerUserName, cancellationToken);
        var ownerName = Normalize(owner?.DisplayName) ??
            Normalize(string.Join(" ", new[] { owner?.FirstName, owner?.LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))));
        if (ownerName is not null)
        {
            userOrDescription = ownerName;
        }
    }

    var composed = PhoneConfigurationChecks.ComposeDescription(compliance, rawText, userOrDescription);
    if (!string.Equals(composed, phone.Description, StringComparison.Ordinal))
    {
        await cucm.UpdatePhoneAsync(phoneName, composed, null, null, cancellationToken);
    }
    return composed;
}

static BuildingPattern RequireBuildingPattern(
    IReadOnlyDictionary<string, BuildingPattern> buildingPatterns,
    string buildingCode) =>
    buildingPatterns.TryGetValue(buildingCode, out var pattern)
        ? pattern
        : throw new InvalidOperationException(
            $"Building '{buildingCode}' is not present in the 'building-patterns' setting.");

static void EnsureRoomDidCanBeAssigned(
    string roomNumber,
    string expectedRoutePartitionName,
    CucmDirectoryNumber? existing)
{
    if (existing is not null &&
        !string.Equals(
            existing.RoutePartitionName,
            expectedRoutePartitionName,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Room DN '{roomNumber}' already exists in partition '{DisplayPartition(existing.RoutePartitionName)}', " +
            $"not the expected '{expectedRoutePartitionName}'. Resolve the conflict in CUCM before assigning it.");
    }
}

static async Task<LineTemplate> RequireLineTemplateAsync(ModuleContext context, string templateName)
{
    var templates = await LoadLineTemplatesAsync(context);
    return templates.TryGetValue(templateName, out var template)
        ? template
        : throw new InvalidOperationException(
            $"Line template '{templateName}' is not present (checked the local template store and " +
            "the 'line-templates' setting).");
}

// Merges the configuration-defined templates ('line-templates' setting) with locally-managed ones
// (created via 'vt cucm templates'); the configuration setting wins on a name clash since it's
// operator-controlled outside the running module.
static async Task<IReadOnlyDictionary<string, LineTemplate>> LoadLineTemplatesAsync(ModuleContext context)
{
    var configTemplates = PhoneConfigurationChecks.ParseLineTemplates(
        context.Configuration.GetValueOrDefault("line-templates") ?? "{}");
    var storedTemplates = await new LineTemplateStore(context.DataDirectory).LoadAsync(context.CancellationToken);
    var merged = new Dictionary<string, LineTemplate>(storedTemplates, StringComparer.OrdinalIgnoreCase);
    foreach (var (name, template) in configTemplates)
    {
        merged[name] = template;
    }
    return merged;
}

// The classroom template needs one unambiguous device pool per building; multi-pool buildings
// aren't supported by this flow yet.
static string RequireClassroomDevicePool(BuildingPattern buildingPattern, string buildingCode) =>
    buildingPattern.DevicePoolNames.Count == 1
        ? buildingPattern.DevicePoolNames[0]
        : throw new InvalidOperationException(
            $"Building '{buildingCode}' must list exactly one device pool in 'building-patterns' to " +
            "use the classroom template.");

static string RequireClassroomPhoneTemplate(ModuleContext context, string? phoneTemplateName)
    => PhoneConfigurationChecks.RequireClassroomPhoneTemplate(
        phoneTemplateName,
        PhoneConfigurationChecks.ParseTemplateCompliancePolicies(
            context.Configuration.GetValueOrDefault("template-compliance-policies") ?? "{}"));

// Creates the line's DN if missing (using the template's voicemail profile), assigns it, then
// applies alerting name/voicemail/caller ID/label/external mask from the template's substituted tokens.
static async Task ApplyClassroomLineAsync(
    ModuleContext context,
    CucmService cucm,
    LineTemplate template,
    VSharp.Cucm.Models.CucmPhone phone,
    string phoneName,
    int lineIndex,
    string pattern,
    string? routePartitionName,
    string devicePoolName,
    string? room,
    string? building,
    string? ownerUserId,
    string? userDisplayName)
{
    string? Substitute(string? value) => Normalize(PhoneConfigurationChecks.SubstituteLineTemplateTokens(
        value, room, building, pattern, phoneName, lineIndex.ToString(), devicePoolName,
        userDisplayName, ownerUserId));

    var alertingName = Substitute(template.AlertingName);
    var display = Substitute(template.Display);
    var label = Substitute(template.Label);
    var externalMask = Substitute(template.ExternalPhoneNumberMask);
    var voiceMailProfileName = Substitute(template.VoiceMailProfileName);

    var existing = await cucm.GetDirectoryNumberAsync(pattern, routePartitionName, context.CancellationToken);
    if (existing is null)
    {
        await cucm.AddDirectoryNumberAsync(
            new CucmDirectoryNumberCreateRequest(
                pattern,
                routePartitionName,
                alertingName ?? label ?? $"{pattern} ({DisplayPartition(routePartitionName)})",
                VoiceMailProfileName: voiceMailProfileName),
            context.CancellationToken);
    }

    var lineExisted = phone.Lines.Any(line => line.Index == lineIndex);
    try
    {
        await cucm.AssignPhoneLineDirectoryNumberAsync(
            phoneName, lineIndex, pattern, routePartitionName, context.CancellationToken);
    }
    catch (Exception assignException) when (!lineExisted)
    {
        throw new InvalidOperationException(
            $"Could not add a new line at index {lineIndex} on '{phoneName}'. This phone's button " +
            $"template ('{Clean(phone.PhoneTemplateName)}') may not define a Line-type button at that " +
            "position. Change the phone's button template via 'phones edit' to one with more line " +
            $"positions, then retry. CUCM error: {assignException.Message}",
            assignException);
    }
    if (alertingName is not null || voiceMailProfileName is not null)
    {
        await cucm.UpdateDirectoryNumberAsync(
            new CucmDirectoryNumberUpdateRequest(
                pattern, routePartitionName,
                AlertingName: alertingName,
                VoiceMailProfileName: voiceMailProfileName),
            context.CancellationToken);
    }
    if (display is not null)
    {
        await cucm.UpdatePhoneLineDisplayAsync(phoneName, lineIndex, display, display, context.CancellationToken);
    }
    if (label is not null)
    {
        await cucm.UpdatePhoneLineLabelAsync(phoneName, lineIndex, label, context.CancellationToken);
    }
    if (externalMask is not null)
    {
        await cucm.UpdatePhoneLineExternalMaskAsync(phoneName, lineIndex, externalMask, context.CancellationToken);
    }
}

// For "user" kind templates: prompts for an owner (if the template calls for one), otherwise
// proceeds straight to the review screen with no owner.
static async Task RespondWithLineTemplateOwnerOrReviewAsync(
    ModuleContext context,
    CucmService cucm,
    LineTemplate template,
    string templateName,
    string phoneName,
    int lineIndex,
    string pattern,
    string? routePartitionName)
{
    if (template.AssociateEndUser)
    {
        var phoneForOwnerPrompt = await RequirePhoneAsync(cucm, phoneName, context.CancellationToken);
        await context.RespondAsync(new ModuleTextPromptResponse(
            $"Owner for {pattern}",
            "Owner user ID (leave blank for none)",
            [
                "phones", "apply-template-user-owner-review", phoneName, lineIndex.ToString(),
                templateName, pattern, routePartitionName ?? string.Empty,
            ],
            phoneForOwnerPrompt.OwnerUserName,
            AllowEmpty: true));
        return;
    }
    var phone = await RequirePhoneAsync(cucm, phoneName, context.CancellationToken);
    await RespondWithLineTemplateReviewAsync(
        context, cucm, template, templateName, phone, phoneName, lineIndex, pattern,
        routePartitionName, ownerUserId: null, room: null, building: null);
}

// Substitutes the template's tokens against the resolved context, then renders a single-row
// "review" table (Save-confirmed) whose row Arguments carry every concrete resolved value forward
// to 'apply-template-apply'. Null substituted fields mean "leave unchanged" and are not written.
static async Task RespondWithLineTemplateReviewAsync(
    ModuleContext context,
    CucmService cucm,
    LineTemplate template,
    string templateName,
    VSharp.Cucm.Models.CucmPhone phone,
    string phoneName,
    int lineIndex,
    string pattern,
    string? routePartitionName,
    string? ownerUserId,
    string? room,
    string? building)
{
    string? userDisplayName = null;
    if (ownerUserId is not null)
    {
        var owner = await RequireUserAsync(cucm, ownerUserId, context.CancellationToken);
        userDisplayName = Normalize(owner.DisplayName) ??
            Normalize(string.Join(
                " ",
                new[] { owner.FirstName, owner.LastName }.Where(part => !string.IsNullOrWhiteSpace(part))));
    }

    string? Substitute(string? value) => Normalize(PhoneConfigurationChecks.SubstituteLineTemplateTokens(
        value, room, building, pattern, phoneName, lineIndex.ToString(), phone.DevicePoolName,
        userDisplayName, ownerUserId));

    var alertingName = Substitute(template.AlertingName);
    var display = Substitute(template.Display);
    var label = Substitute(template.Label);
    var externalMask = Substitute(template.ExternalPhoneNumberMask);
    var voiceMailProfileName = Substitute(template.VoiceMailProfileName);

    var existing = await cucm.GetDirectoryNumberAsync(pattern, routePartitionName, context.CancellationToken);

    await context.RespondAsync(new ModuleTableResponse(
        $"Review template '{templateName}' for line {lineIndex} on {phoneName}",
        [
            "DN", "PARTITION", "DN ACTION", "ALERTING NAME", "CALLER ID", "LABEL",
            "EXTERNAL MASK", "VOICEMAIL PROFILE", "OWNER",
        ],
        [
            new ModuleTableRow(
                "submit",
                [
                    pattern,
                    DisplayPartition(routePartitionName),
                    existing is null ? "Create in CUCM" : "Use existing CUCM DN",
                    alertingName ?? "<unchanged>",
                    display ?? "<unchanged>",
                    label ?? "<unchanged>",
                    externalMask ?? "<unchanged>",
                    voiceMailProfileName ?? "<unchanged>",
                    ownerUserId is null ? "<unchanged>" : $"{ownerUserId} ({OwnerActionLabel(phone.OwnerUserName, ownerUserId)})",
                ],
                [
                    "phones", "apply-template-apply", phoneName, lineIndex.ToString(), pattern,
                    routePartitionName ?? string.Empty, alertingName ?? string.Empty, display ?? string.Empty,
                    label ?? string.Empty, externalMask ?? string.Empty, voiceMailProfileName ?? string.Empty,
                    ownerUserId ?? string.Empty,
                ]),
        ],
        SubmitMode: ModuleTableSubmitMode.Save));
}

static async ValueTask<int> DirectoryNumbersAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            var rows = new List<ModuleTableRow>();
            await foreach (var line in cucm.ListDirectoryNumbersAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var rowPattern = line.Pattern ?? string.Empty;
                var rowPartition = line.RoutePartitionName ?? string.Empty;
                rows.Add(new ModuleTableRow(
                    line.Uuid ?? $"{rowPattern}:{rowPartition}",
                    [
                        Clean(rowPattern),
                        Clean(rowPartition),
                        Clean(line.Description),
                        Clean(line.CallingSearchSpaceName),
                        Clean(line.VoiceMailProfileName),
                    ],
                    string.IsNullOrWhiteSpace(rowPattern)
                        ? null
                        : DirectoryNumberPatternArguments("select", rowPattern, rowPartition)));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM directory numbers",
                ["PATTERN", "PARTITION", "DESCRIPTION", "CSS", "VOICEMAIL PROFILE"],
                rows));
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "select",
            out var selectPattern,
            out var selectPartition))
        {
            var selected = await RequireDirectoryNumberAsync(
                cucm,
                selectPattern,
                selectPartition,
                context.CancellationToken);
            var selectedPartition = selected.RoutePartitionName ?? selectPartition;
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM directory number: {selected.Pattern}",
                ["ACTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "info",
                        ["View details", Clean(selected.Description)],
                        DirectoryNumberPatternArguments("info", selectPattern, selectedPartition)),
                    new ModuleTableRow(
                        "edit",
                        [
                            "Edit",
                            "Update description, CSS, voicemail profile, and forwarding",
                        ],
                        DirectoryNumberPatternArguments("edit", selectPattern, selectedPartition)),
                    new ModuleTableRow(
                        "delete",
                        ["Delete", "Remove this directory number from CUCM"],
                        DirectoryNumberPatternArguments("delete", selectPattern, selectedPartition)),
                ]));
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "info",
            out var infoPattern,
            out var infoPartition))
        {
            var line = await RequireDirectoryNumberAsync(
                cucm,
                infoPattern,
                infoPartition,
                context.CancellationToken);
            await RespondWithDirectoryNumberInfoAsync(context, line);
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "edit",
            out var editPattern,
            out var editPartition))
        {
            var current = await RequireDirectoryNumberAsync(
                cucm,
                editPattern,
                editPartition,
                context.CancellationToken);
            var editState = new DnWizardState(
                current.Pattern ?? editPattern,
                current.Description,
                current.RoutePartitionName ?? editPartition,
                current.CallingSearchSpaceName,
                current.VoiceMailProfileName,
                current.CallForwardAll.Destination,
                current.CallForwardAll.CallingSearchSpaceName,
                current.CallForwardAll.ForwardToVoiceMail);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Edit directory number {editState.Pattern}",
                "Description",
                WizardArguments("edit-css", editState),
                editState.Description,
                AllowEmpty: true));
            return 0;
        }
        if (TryParseWizardStateWithOptionalValue(context.Arguments, "edit-css", out var editCssBaseState, out var editDescription))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select line calling search space",
                "edit-voicemail-profile",
                editCssBaseState with { Description = Normalize(editDescription) },
                "css");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-voicemail-profile", out var editCssState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select voicemail profile",
                "edit-forward",
                editCssState,
                "voicemail-profile");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-forward", out var editVoicemailState))
        {
            await RespondWithForwardingSelectorAsync(
                context,
                editVoicemailState,
                "edit-review",
                "edit-forward-destination",
                "edit-forward-voicemail");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "edit-forward-destination",
            out var editDestinationState))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Configure forward all",
                "Forward destination",
                WizardArguments("edit-forward-destination-value", editDestinationState)));
            return 0;
        }
        if (TryParseWizardStateWithValue(
            context.Arguments,
            "edit-forward-destination-value",
            out var editDestinationValueState,
            out var editForwardDestination))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "edit-review",
                editDestinationValueState with { ForwardDestination = editForwardDestination },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "edit-forward-voicemail",
            out var editVoicemailForwardState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "edit-review",
                editVoicemailForwardState with { ForwardToVoiceMail = true },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-review", out var editReviewState))
        {
            await RespondWithDirectoryNumberReviewAsync(
                context,
                editReviewState,
                "Review directory number changes",
                "edit-update");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-update", out var editUpdateState))
        {
            await cucm.UpdateDirectoryNumberAsync(
                editUpdateState.ToUpdateRequest(),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated directory number '{editUpdateState.Pattern}' in partition " +
                $"'{DisplayPartition(editUpdateState.RoutePartitionName)}'.");
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "delete",
            out var deletePattern,
            out var deletePartition))
        {
            var toDelete = await RequireDirectoryNumberAsync(
                cucm,
                deletePattern,
                deletePartition,
                context.CancellationToken);
            var deletePartitionResolved = toDelete.RoutePartitionName ?? deletePartition;
            await context.RespondAsync(new ModuleTableResponse(
                "Confirm delete",
                ["PATTERN", "PARTITION", "DESCRIPTION"],
                [
                    new ModuleTableRow(
                        "confirm",
                        [
                            Clean(toDelete.Pattern),
                            DisplayPartition(deletePartitionResolved),
                            Clean(toDelete.Description),
                        ],
                        [
                            "dn", "delete-confirm", deletePattern,
                            deletePartitionResolved ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["delete-confirm", var confirmPattern, var confirmPartitionRaw])
        {
            var confirmPartition = Normalize(confirmPartitionRaw);
            await cucm.DeleteDirectoryNumberAsync(
                confirmPattern,
                confirmPartition,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Deleted directory number '{confirmPattern}' from partition " +
                $"'{DisplayPartition(confirmPartition)}'.");
            return 0;
        }
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add CUCM directory number",
                "Directory number",
                ["dn", "add-description"]));
            return 0;
        }
        if (context.Arguments is ["add-description", var descriptionPattern])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add directory number {descriptionPattern}",
                "Description",
                ["dn", "add-partition", descriptionPattern],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["add-partition", var partitionPattern, var description])
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select route partition",
                "add-css",
                new DnWizardState(partitionPattern, description),
                "partition");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-css", out var partitionState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select line calling search space",
                "add-voicemail-profile",
                partitionState,
                "css");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "add-voicemail-profile",
            out var cssState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select voicemail profile",
                "add-forward",
                cssState,
                "voicemail-profile");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-forward", out var forwardingState))
        {
            await RespondWithForwardingSelectorAsync(
                context,
                forwardingState,
                "add-review",
                "add-forward-destination",
                "add-forward-voicemail");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "add-forward-destination",
            out var destinationState))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Configure forward all",
                "Forward destination",
                WizardArguments("add-forward-destination-value", destinationState)));
            return 0;
        }
        if (TryParseWizardStateWithValue(
            context.Arguments,
            "add-forward-destination-value",
            out var destinationValueState,
            out var forwardDestination))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "add-review",
                destinationValueState with { ForwardDestination = forwardDestination },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "add-forward-voicemail",
            out var voicemailForwardState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "add-review",
                voicemailForwardState with { ForwardToVoiceMail = true },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-review", out var reviewState))
        {
            await RespondWithDirectoryNumberReviewAsync(
                context,
                reviewState,
                "Review directory number",
                "add-create");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-create", out var createState))
        {
            var uuid = await cucm.AddDirectoryNumberAsync(
                createState.ToRequest(),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Created directory number '{createState.Pattern}' in partition " +
                $"'{DisplayPartition(createState.RoutePartitionName)}'" +
                (string.IsNullOrWhiteSpace(uuid) ? "." : $" ({uuid})."));
            return 0;
        }
        if (context.Arguments.Count > 1 && context.Arguments[0] == "add")
        {
            if (!TryParseDirectoryNumberAddArguments(
                context.Arguments.Skip(1).ToArray(),
                out var request,
                out var error))
            {
                context.Error.WriteLine(error);
                WriteDirectoryNumberUsage(context);
                return 2;
            }
            var uuid = await cucm.AddDirectoryNumberAsync(request, context.CancellationToken);
            context.Output.WriteLine(
                $"Created directory number '{request.Pattern}' in partition " +
                $"'{DisplayPartition(request.RoutePartitionName)}'" +
                (string.IsNullOrWhiteSpace(uuid) ? "." : $" ({uuid})."));
            return 0;
        }

        WriteDirectoryNumberUsage(context);
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task RespondWithDirectoryNumberInfoAsync(
    ModuleContext context,
    CucmDirectoryNumber line)
{
    var cfa = line.CallForwardAll;
    var forwarding = cfa.ForwardToVoiceMail
        ? "Voicemail"
        : string.IsNullOrWhiteSpace(cfa.Destination)
            ? "Disabled"
            : cfa.Destination;
    await context.RespondAsync(new ModuleTableResponse(
        $"CUCM directory number: {line.Pattern}",
        ["PROPERTY", "VALUE"],
        [
            new ModuleTableRow("pattern", ["Pattern", Clean(line.Pattern)]),
            new ModuleTableRow(
                "partition",
                ["Route partition", DisplayPartition(line.RoutePartitionName)]),
            new ModuleTableRow("description", ["Description", Clean(line.Description)]),
            new ModuleTableRow("css", ["Calling search space", Clean(line.CallingSearchSpaceName)]),
            new ModuleTableRow(
                "voicemail-profile",
                ["Voicemail profile", Clean(line.VoiceMailProfileName)]),
            new ModuleTableRow("forward-all", ["Forward all", Clean(forwarding)]),
            new ModuleTableRow(
                "forward-css",
                ["Forward-all CSS", Clean(cfa.CallingSearchSpaceName)]),
        ],
        Selectable: false,
        Searchable: false));
}

static async Task RespondWithNamedSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    string title,
    string nextRoute,
    DnWizardState state,
    string resourceType)
{
    var rows = new List<ModuleTableRow>
    {
        new($"none:{resourceType}", ["<None>", string.Empty], WizardArguments(nextRoute, state)),
    };
    var resources = resourceType switch
    {
        "partition" => cucm.ListRoutePartitionsAsync(cancellationToken: context.CancellationToken),
        "css" or "forward-css" => cucm.ListCallingSearchSpacesAsync(
            cancellationToken: context.CancellationToken),
        "voicemail-profile" => cucm.ListVoiceMailProfilesAsync(
            cancellationToken: context.CancellationToken),
        _ => throw new InvalidOperationException($"Unknown CUCM resource type '{resourceType}'."),
    };
    await foreach (var resource in resources)
    {
        if (string.IsNullOrWhiteSpace(resource.Name))
        {
            continue;
        }
        rows.Add(new ModuleTableRow(
            resource.Uuid ?? $"{resourceType}:{resource.Name}",
            [Clean(resource.Name), Clean(resource.Description)],
            WizardArguments(nextRoute, SetWizardResource(state, resourceType, resource.Name))));
    }
    await context.RespondAsync(new ModuleTableResponse(
        title,
        ["NAME", "DESCRIPTION"],
        rows,
        SubmitMode: ModuleTableSubmitMode.Save));
}

static Task RespondWithForwardingSelectorAsync(
    ModuleContext context,
    DnWizardState state,
    string disabledRoute,
    string destinationRoute,
    string voicemailRoute) =>
    context.RespondAsync(new ModuleTableResponse(
        "Configure forward all",
        ["OPTION", "DETAIL"],
        [
            new ModuleTableRow(
                "disabled",
                ["Disabled", "Do not forward all calls"],
                WizardArguments(disabledRoute, state)),
            new ModuleTableRow(
                "destination",
                ["Destination", "Forward all calls to a number"],
                WizardArguments(destinationRoute, state)),
            new ModuleTableRow(
                "voicemail",
                ["Voicemail", "Forward all calls to voicemail"],
                WizardArguments(voicemailRoute, state)),
        ],
        SubmitMode: ModuleTableSubmitMode.Save)).AsTask();

static Task RespondWithDirectoryNumberReviewAsync(
    ModuleContext context,
    DnWizardState state,
    string title,
    string submitRoute)
{
    var forwarding = state.ForwardToVoiceMail
        ? "Voicemail"
        : string.IsNullOrWhiteSpace(state.ForwardDestination)
            ? "Disabled"
            : state.ForwardDestination;
    return context.RespondAsync(new ModuleTableResponse(
        title,
        ["PATTERN", "PARTITION", "DESCRIPTION", "CSS", "VOICEMAIL", "FORWARD ALL", "FORWARD CSS"],
        [
            new ModuleTableRow(
                "submit",
                [
                    Clean(state.Pattern),
                    DisplayPartition(state.RoutePartitionName),
                    Clean(state.Description),
                    Clean(state.CallingSearchSpaceName),
                    Clean(state.VoiceMailProfileName),
                    Clean(forwarding),
                    Clean(state.ForwardCallingSearchSpaceName),
                ],
                WizardArguments(submitRoute, state)),
            ],
            SubmitMode: ModuleTableSubmitMode.Save)).AsTask();
}

static DnWizardState SetWizardResource(
    DnWizardState state,
    string resourceType,
    string value) => resourceType switch
{
    "partition" => state with { RoutePartitionName = value },
    "css" => state with { CallingSearchSpaceName = value },
    "voicemail-profile" => state with { VoiceMailProfileName = value },
    "forward-css" => state with { ForwardCallingSearchSpaceName = value },
    _ => throw new InvalidOperationException($"Unknown CUCM resource type '{resourceType}'."),
};

static async Task RespondWithPhoneNamedSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    string title,
    string nextRoute,
    PhoneWizardState state,
    string resourceType,
    bool allowNone)
{
    var rows = new List<ModuleTableRow>();
    if (allowNone)
    {
        rows.Add(new ModuleTableRow(
            $"none:{resourceType}",
            ["<None>", string.Empty],
            PhoneWizardArguments(nextRoute, state)));
    }
    var resources = resourceType switch
    {
        "device-pool" => cucm.ListDevicePoolsAsync(cancellationToken: context.CancellationToken),
        "phone-template" => cucm.ListPhoneButtonTemplatesAsync(
            cancellationToken: context.CancellationToken),
        "security-profile" => cucm.ListPhoneSecurityProfilesAsync(
            cancellationToken: context.CancellationToken),
        _ => throw new InvalidOperationException(
            $"Unknown CUCM phone resource type '{resourceType}'."),
    };
    await foreach (var resource in resources)
    {
        if (string.IsNullOrWhiteSpace(resource.Name))
        {
            continue;
        }
        rows.Add(new ModuleTableRow(
            resource.Uuid ?? $"{resourceType}:{resource.Name}",
            [Clean(resource.Name), Clean(resource.Description)],
            PhoneWizardArguments(nextRoute, SetPhoneWizardResource(state, resourceType, resource.Name))));
    }
    await context.RespondAsync(new ModuleTableResponse(
        title,
        ["NAME", "DESCRIPTION"],
        rows,
        SubmitMode: ModuleTableSubmitMode.Save));
}

static PhoneWizardState SetPhoneWizardResource(
    PhoneWizardState state,
    string resourceType,
    string value) => resourceType switch
{
    "device-pool" => state with { DevicePoolName = value },
    "phone-template" => state with { PhoneTemplateName = value },
    "security-profile" => state with { SecurityProfileName = value },
    _ => throw new InvalidOperationException($"Unknown CUCM phone resource type '{resourceType}'."),
};

static Task RespondWithPhoneCreateReviewAsync(ModuleContext context, PhoneWizardState state) =>
    context.RespondAsync(new ModuleTableResponse(
        "Review new phone",
        ["NAME", "DESCRIPTION", "PRODUCT", "DEVICE POOL", "PHONE TEMPLATE", "SECURITY PROFILE", "OWNER"],
        [
            new ModuleTableRow(
                "submit",
                [
                    Clean(state.Name),
                    Clean(state.Description),
                    Clean(state.Product),
                    Clean(state.DevicePoolName),
                    Clean(state.PhoneTemplateName),
                    Clean(state.SecurityProfileName),
                    Clean(state.OwnerUserName),
                ],
                PhoneWizardArguments("add-create", state)),
        ],
        SubmitMode: ModuleTableSubmitMode.Save)).AsTask();

static IReadOnlyList<string> PhoneWizardArguments(string route, PhoneWizardState state) =>
    [
        "phones",
        route,
        state.Name,
        state.Description ?? string.Empty,
        state.Product ?? string.Empty,
        state.DevicePoolName ?? string.Empty,
        state.PhoneTemplateName ?? string.Empty,
        state.SecurityProfileName ?? string.Empty,
        state.OwnerUserName ?? string.Empty,
    ];

static bool TryParsePhoneWizardState(
    IReadOnlyList<string> arguments,
    string route,
    out PhoneWizardState state)
{
    state = new PhoneWizardState(string.Empty);
    if (arguments.Count != 8 || arguments[0] != route)
    {
        return false;
    }
    state = new PhoneWizardState(
        arguments[1],
        Normalize(arguments[2]),
        Normalize(arguments[3]),
        Normalize(arguments[4]),
        Normalize(arguments[5]),
        Normalize(arguments[6]),
        Normalize(arguments[7]));
    return true;
}

static bool TryParsePhoneWizardStateWithValue(
    IReadOnlyList<string> arguments,
    string route,
    out PhoneWizardState state,
    out string value)
{
    state = new PhoneWizardState(string.Empty);
    value = string.Empty;
    if (arguments.Count != 9 || !TryParsePhoneWizardState(arguments.Take(8).ToArray(), route, out state))
    {
        return false;
    }
    value = arguments[8];
    return true;
}

static IReadOnlyList<string> DirectoryNumberPatternArguments(
    string verb,
    string pattern,
    string? partition) =>
    string.IsNullOrWhiteSpace(partition)
        ? ["dn", verb, pattern]
        : ["dn", verb, pattern, "--partition", partition];

static bool TryParseDirectoryNumberPatternArguments(
    IReadOnlyList<string> arguments,
    string verb,
    out string pattern,
    out string? partition)
{
    pattern = string.Empty;
    partition = null;
    if (arguments.Count < 2 || arguments[0] != verb || string.IsNullOrWhiteSpace(arguments[1]))
    {
        return false;
    }
    pattern = arguments[1];
    if (arguments.Count == 2)
    {
        return true;
    }
    if (arguments.Count == 4 && arguments[2] == "--partition")
    {
        partition = arguments[3];
        return true;
    }
    return false;
}

static async Task<CucmDirectoryNumber> RequireDirectoryNumberAsync(
    CucmService cucm,
    string pattern,
    string? partition,
    CancellationToken cancellationToken) =>
    await cucm.GetDirectoryNumberAsync(pattern, partition, cancellationToken) ??
        throw new InvalidOperationException(
            $"CUCM directory number '{pattern}' in partition " +
            $"'{DisplayPartition(partition)}' was not found.");


static bool TryParseDirectoryNumberAddArguments(
    IReadOnlyList<string> arguments,
    out CucmDirectoryNumberCreateRequest request,
    out string error)
{
    request = new CucmDirectoryNumberCreateRequest(string.Empty);
    error = string.Empty;
    if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]))
    {
        error = "A directory-number pattern is required.";
        return false;
    }

    string? partition = null;
    string? description = null;
    string? css = null;
    string? voicemailProfile = null;
    string? forwardDestination = null;
    string? forwardCss = null;
    var forwardToVoiceMail = false;
    for (var index = 1; index < arguments.Count; index++)
    {
        var option = arguments[index];
        if (option == "--forward-to-voicemail")
        {
            forwardToVoiceMail = true;
            continue;
        }
        if (index + 1 >= arguments.Count)
        {
            error = $"Option '{option}' requires a value.";
            return false;
        }
        var value = arguments[++index];
        switch (option)
        {
            case "--partition":
                partition = value;
                break;
            case "--description":
                description = value;
                break;
            case "--css":
                css = value;
                break;
            case "--voicemail-profile":
                voicemailProfile = value;
                break;
            case "--forward-destination":
                forwardDestination = value;
                break;
            case "--forward-css":
                forwardCss = value;
                break;
            default:
                error = $"Unknown directory-number option '{option}'.";
                return false;
        }
    }
    if (forwardToVoiceMail && !string.IsNullOrWhiteSpace(forwardDestination))
    {
        error = "Use either --forward-to-voicemail or --forward-destination, not both.";
        return false;
    }
    if (!string.IsNullOrWhiteSpace(forwardCss) &&
        !forwardToVoiceMail &&
        string.IsNullOrWhiteSpace(forwardDestination))
    {
        error = "--forward-css requires --forward-destination or --forward-to-voicemail.";
        return false;
    }

    var callForwardAll = forwardToVoiceMail || !string.IsNullOrWhiteSpace(forwardDestination)
        ? new CucmCallForwardSettings(
            forwardToVoiceMail,
            Normalize(forwardCss),
            Destination: Normalize(forwardDestination))
        : null;
    request = new CucmDirectoryNumberCreateRequest(
        arguments[0],
        Normalize(partition),
        Normalize(description),
        Normalize(css),
        Normalize(voicemailProfile),
        callForwardAll);
    return true;
}

static IReadOnlyList<string> WizardArguments(string route, DnWizardState state) =>
    [
        "dn",
        route,
        state.Pattern,
        state.Description ?? string.Empty,
        state.RoutePartitionName ?? string.Empty,
        state.CallingSearchSpaceName ?? string.Empty,
        state.VoiceMailProfileName ?? string.Empty,
        state.ForwardDestination ?? string.Empty,
        state.ForwardCallingSearchSpaceName ?? string.Empty,
        state.ForwardToVoiceMail.ToString(),
    ];

static bool TryParseWizardState(
    IReadOnlyList<string> arguments,
    string route,
    out DnWizardState state)
{
    state = new DnWizardState(string.Empty);
    if (arguments.Count != 9 || arguments[0] != route)
    {
        return false;
    }
    state = new DnWizardState(
        arguments[1],
        Normalize(arguments[2]),
        Normalize(arguments[3]),
        Normalize(arguments[4]),
        Normalize(arguments[5]),
        Normalize(arguments[6]),
        Normalize(arguments[7]),
        bool.TryParse(arguments[8], out var forwardToVoiceMail) && forwardToVoiceMail);
    return true;
}

static bool TryParseWizardStateWithValue(
    IReadOnlyList<string> arguments,
    string route,
    out DnWizardState state,
    out string value)
{
    state = new DnWizardState(string.Empty);
    value = string.Empty;
    if (arguments.Count != 10 || !TryParseWizardState(arguments.Take(9).ToArray(), route, out state))
    {
        return false;
    }
    value = arguments[9];
    return !string.IsNullOrWhiteSpace(value);
}

// Same shape as TryParseWizardStateWithValue, but for continuations whose value is allowed to
// be blank (e.g. clearing a description), unlike a required field such as forward destination.
static bool TryParseWizardStateWithOptionalValue(
    IReadOnlyList<string> arguments,
    string route,
    out DnWizardState state,
    out string value)
{
    state = new DnWizardState(string.Empty);
    value = string.Empty;
    if (arguments.Count != 10 || !TryParseWizardState(arguments.Take(9).ToArray(), route, out state))
    {
        return false;
    }
    value = arguments[9];
    return true;
}

static void WriteDirectoryNumberUsage(ModuleContext context) =>
    context.Error.WriteLine(
        "Usage: vt cucm dn list [--max <count>] [--page-size <count>]\n" +
        "       vt cucm dn info <pattern> [--partition <name>]\n" +
        "       vt cucm dn edit <pattern> [--partition <name>]\n" +
        "       vt cucm dn delete <pattern> [--partition <name>]\n" +
        "       vt cucm dn add [<pattern> [--partition <name>] [--description <text>] " +
        "[--css <name>] [--voicemail-profile <name>] [--forward-destination <number>] " +
        "[--forward-css <name>] [--forward-to-voicemail]]");

static string DisplayPartition(string? partition) =>
    string.IsNullOrWhiteSpace(partition) ? "<None>" : Clean(partition);

static string? Normalize(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;

static string FormatProfile(PhoneConfigurationProfile profile) => profile switch
{
    PhoneConfigurationProfile.BasicRoom => "Basic room",
    PhoneConfigurationProfile.Classroom => "Classroom",
    PhoneConfigurationProfile.RoomRouting => "Room routing",
    _ => profile.ToString(),
};

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
    // A bare command (no arguments) is treated the same as an explicit "list".
    if (arguments.Count > 0 && arguments[0] != "list")
    {
        return false;
    }
    var startIndex = arguments.Count > 0 ? 1 : 0;
    for (var index = startIndex; index < arguments.Count; index++)
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

sealed record DnWizardState(
    string Pattern,
    string? Description = null,
    string? RoutePartitionName = null,
    string? CallingSearchSpaceName = null,
    string? VoiceMailProfileName = null,
    string? ForwardDestination = null,
    string? ForwardCallingSearchSpaceName = null,
    bool ForwardToVoiceMail = false)
{
    public CucmDirectoryNumberCreateRequest ToRequest()
    {
        var callForwardAll = ForwardToVoiceMail || !string.IsNullOrWhiteSpace(ForwardDestination)
            ? new CucmCallForwardSettings(
                ForwardToVoiceMail,
                ForwardCallingSearchSpaceName,
                Destination: ForwardDestination)
            : null;
        return new CucmDirectoryNumberCreateRequest(
            Pattern,
            RoutePartitionName,
            Description,
            CallingSearchSpaceName,
            VoiceMailProfileName,
            callForwardAll);
    }

    public CucmDirectoryNumberUpdateRequest ToUpdateRequest()
    {
        var callForwardAll = ForwardToVoiceMail || !string.IsNullOrWhiteSpace(ForwardDestination)
            ? new CucmCallForwardSettings(
                ForwardToVoiceMail,
                ForwardCallingSearchSpaceName,
                Destination: ForwardDestination)
            : null;
        return new CucmDirectoryNumberUpdateRequest(
            Pattern,
            RoutePartitionName,
            Description,
            CallingSearchSpaceName,
            VoiceMailProfileName,
            callForwardAll);
    }
}

sealed record PhoneWizardState(
    string Name,
    string? Description = null,
    string? Product = null,
    string? DevicePoolName = null,
    string? PhoneTemplateName = null,
    string? SecurityProfileName = null,
    string? OwnerUserName = null)
{
    public CucmPhoneCreateRequest ToCreateRequest() =>
        new(
            Name,
            Product ?? string.Empty,
            DevicePoolName ?? string.Empty,
            Description,
            PhoneTemplateName: PhoneTemplateName,
            SecurityProfileName: SecurityProfileName,
            OwnerUserName: OwnerUserName);
}

sealed record ProvisionWizardState(
    bool IsNewPhone,
    string PhoneName,
    string? Description = null,
    string? Product = null,
    string? DevicePoolName = null,
    string? PhoneTemplateName = null,
    string? SecurityProfileName = null,
    string? UserId = null,
    string? PreviousOwnerUserName = null);

sealed record PhoneExportRecord(
    string Name,
    string? Description,
    string? Product,
    string? Model,
    string? Protocol,
    string? Owner,
    string? DevicePool,
    string? PhoneTemplate,
    string? SecurityProfile,
    string Lines);

