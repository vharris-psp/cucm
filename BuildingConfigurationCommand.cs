using VSharp.Cucm;
using Vt.ModuleSdk;

internal static class BuildingConfigurationCommand
{
    internal static async ValueTask<ModuleCommandOutcome> ExecuteAsync(
        ModuleContext context,
        CucmService cucm)
    {
        if (LoadingStatus(context.Arguments) is { } loadingStatus)
        {
            await context.ReportProgressAsync(loadingStatus, 0, 1);
        }

        var store = new BuildingProfileStore(context.DataDirectory);
        var resourceQueries = new CucmResourceQueryService(cucm);
        var legacyConfiguration = context.Configuration.GetValueOrDefault("building-patterns");

        if (context.Arguments.Count == 0)
        {
            return ModuleCommandResult.Render(new ModuleTableResponse(
                "CUCM configuration",
                ["AREA", "DESCRIPTION"],
                [
                    new ModuleTableRow(
                        "buildings",
                        ["Buildings", "Manage classroom routing, device pools, and phone templates"],
                        ["configure", "buildings"]),
                    new ModuleTableRow(
                        "defaults",
                        ["Defaults", "Manage user DID partition/CSS/voicemail/forward defaults with live selectors"],
                        ["configure", "defaults"]),
                ]));
        }

        if (context.Arguments is ["buildings"])
        {
            var profiles = await store.LoadEffectiveAsync(
                legacyConfiguration,
                context.CancellationToken);
            var source = store.Exists ? "Local profile store" : "Legacy module setting";
            var rows = profiles
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ModuleTableRow(
                    pair.Key,
                    [
                        pair.Key,
                        pair.Value.RoutePartitionName,
                        pair.Value.DevicePoolName ?? "<not configured>",
                        string.Join(", ", pair.Value.DevicePoolNames),
                        pair.Value.PhoneTemplateName ?? "<phone default>",
                        source,
                    ],
                    ["configure", "buildings", "select", pair.Key]))
                .ToList();
            rows.Add(new ModuleTableRow(
                "add",
                ["<Add building>", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty],
                ["configure", "buildings", "add"]));
            if (!string.IsNullOrWhiteSpace(legacyConfiguration) &&
                PhoneConfigurationChecks.ParseBuildingPatterns(legacyConfiguration).Count > 0)
            {
                rows.Add(new ModuleTableRow(
                    "import",
                    [
                        store.Exists ? "<Re-import legacy setting>" : "<Import legacy setting>",
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "Review before saving",
                    ],
                    ["configure", "buildings", "import-review"]));
            }
            return ModuleCommandResult.Render(new ModuleTableResponse(
                "Building profiles",
                ["CODE", "ROOM PARTITION", "TARGET POOL", "RECOGNIZED POOLS", "PHONE TEMPLATE", "SOURCE"],
                rows));
        }

        if (context.Arguments is ["buildings", "select", var selectedCode])
        {
            var profiles = await store.LoadEffectiveAsync(
                legacyConfiguration,
                context.CancellationToken);
            var profile = RequireProfile(profiles, selectedCode);
            var rows = new List<ModuleTableRow>
            {
                new("code", ["Code", selectedCode]),
                new("partition", ["Room route partition", profile.RoutePartitionName]),
                new("target-pool", ["Classroom target device pool", profile.DevicePoolName ?? "<not configured>"]),
                new("recognized-pools", ["Recognized device pools", string.Join(", ", profile.DevicePoolNames)]),
                new("template", ["Phone button template", profile.PhoneTemplateName ?? "<phone default>"]),
                new(
                    "room-mask",
                    ["Room external phone number mask", profile.RoomExternalPhoneNumberMask ?? "<not configured>"]),
                new("all-call", ["All Call number", profile.AllCallNumber ?? "<not configured>"]),
                new(
                    "edit",
                    ["Edit", "Select live CUCM resources and review the complete profile"],
                    ["configure", "buildings", "edit-partition", selectedCode]),
            };
            if (store.Exists)
            {
                rows.Add(new ModuleTableRow(
                    "delete",
                    ["Delete", "Remove this local building profile"],
                    ["configure", "buildings", "delete-review", selectedCode]));
            }
            return ModuleCommandResult.Render(new ModuleTableResponse(
                $"Building profile: {selectedCode}",
                ["FIELD", "VALUE"],
                rows));
        }

        if (context.Arguments is ["buildings", "add"])
        {
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                "Add building profile",
                "Building code (for example CE, PMS, or PHS)",
                ["configure", "buildings", "add-code"]));
        }

        if (context.Arguments is ["buildings", "add-code", var rawCode])
        {
            var code = NormalizeCode(rawCode);
            var profiles = await store.LoadEffectiveAsync(
                legacyConfiguration,
                context.CancellationToken);
            if (profiles.ContainsKey(code))
            {
                throw new InvalidOperationException(
                    $"Building '{code}' already exists. Select it and choose Edit.");
            }
            return ModuleCommandResult.Render(await CreatePartitionSelectorAsync(
                resourceQueries,
                code,
                context.CancellationToken));
        }

        if (context.Arguments is ["buildings", "edit-partition", var editCode])
        {
            return ModuleCommandResult.Render(await CreatePartitionSelectorAsync(
                resourceQueries,
                NormalizeCode(editCode),
                context.CancellationToken));
        }

        if (context.Arguments is ["buildings", "edit-device-pool", var poolCode, var partitionName])
        {
            var rows = new List<ModuleTableRow>();
            foreach (var pool in await resourceQueries.ListDevicePoolsAsync(context.CancellationToken))
            {
                rows.Add(new ModuleTableRow(
                    pool.Uuid ?? $"device-pool:{pool.Name}",
                    [pool.Name, Clean(pool.Description)],
                    ["configure", "buildings", "edit-recognized", poolCode, partitionName, pool.Name]));
            }
            return ModuleCommandResult.Render(new ModuleTableResponse(
                $"Classroom target device pool for {poolCode}",
                ["NAME", "DESCRIPTION"],
                rows));
        }

        if (context.Arguments is
            ["buildings", "edit-recognized", var recognizedCode, var recognizedPartition, var targetPool])
        {
            var profiles = await store.LoadEffectiveAsync(
                legacyConfiguration,
                context.CancellationToken);
            var defaultPools = profiles.TryGetValue(recognizedCode, out var current)
                ? string.Join(",", current.DevicePoolNames.Where(name =>
                    !name.Equals(targetPool, StringComparison.OrdinalIgnoreCase)))
                : string.Empty;
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                $"Additional recognized device pools for {recognizedCode}",
                "Comma-separated device pools already used by this building (target pool is added automatically)",
                [
                    "configure", "buildings", "edit-template", recognizedCode,
                    recognizedPartition, targetPool,
                ],
                defaultPools,
                AllowEmpty: true));
        }

        if (context.Arguments is
            [
                "buildings", "edit-template", var templateCode, var templatePartition,
                var templateTargetPool, var recognizedPoolsRaw,
            ])
        {
            var rows = new List<ModuleTableRow>();
            foreach (var template in await resourceQueries.ListPhoneButtonTemplatesAsync(context.CancellationToken))
            {
                rows.Add(new ModuleTableRow(
                    template.Uuid ?? $"phone-template:{template.Name}",
                    [template.Name, Clean(template.Description)],
                    [
                        "configure", "buildings", "edit-mask", templateCode,
                        templatePartition, templateTargetPool, recognizedPoolsRaw, template.Name,
                    ]));
            }
            return ModuleCommandResult.Render(new ModuleTableResponse(
                $"Phone button template for {templateCode}",
                ["NAME", "DESCRIPTION"],
                rows));
        }

        if (context.Arguments is
            [
                "buildings", "edit-mask", var maskCode, var maskPartition,
                var maskTargetPool, var maskPoolsRaw, var maskTemplate,
            ])
        {
            var profiles = await store.LoadEffectiveAsync(legacyConfiguration, context.CancellationToken);
            var currentMask = profiles.TryGetValue(NormalizeCode(maskCode), out var maskProfile)
                ? maskProfile.RoomExternalPhoneNumberMask
                : null;
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                $"Room external phone number mask for {maskCode}",
                "External phone number mask for this building's room DNs (leave blank to configure later)",
                [
                    "configure", "buildings", "edit-allcall", maskCode,
                    maskPartition, maskTargetPool, maskPoolsRaw, maskTemplate,
                ],
                currentMask,
                AllowEmpty: true));
        }

        if (context.Arguments is
            [
                "buildings", "edit-allcall", var allCallCode, var allCallPartition,
                var allCallTargetPool, var allCallPoolsRaw, var allCallTemplate, var allCallMaskRaw,
            ])
        {
            var profiles = await store.LoadEffectiveAsync(legacyConfiguration, context.CancellationToken);
            var currentAllCall = profiles.TryGetValue(NormalizeCode(allCallCode), out var allCallProfile)
                ? allCallProfile.AllCallNumber
                : null;
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                $"All Call number for {allCallCode}",
                "Speed-dial destination for this building's All Call button (leave blank to configure later)",
                [
                    "configure", "buildings", "edit-review", allCallCode,
                    allCallPartition, allCallTargetPool, allCallPoolsRaw, allCallTemplate, allCallMaskRaw,
                ],
                currentAllCall,
                AllowEmpty: true));
        }

        if (context.Arguments is
            [
                "buildings", "edit-review", var reviewCode, var reviewPartition,
                var reviewTargetPool, var reviewPoolsRaw, var reviewTemplate, var reviewMaskRaw, var reviewAllCallRaw,
            ])
        {
            var profiles = await store.LoadEffectiveAsync(
                legacyConfiguration,
                context.CancellationToken);
            var profile = CreateProfile(
                reviewPartition,
                reviewTargetPool,
                reviewPoolsRaw,
                reviewTemplate,
                reviewMaskRaw,
                reviewAllCallRaw);
            EnsurePoolsAreUnambiguous(profiles, reviewCode, profile);
            return ModuleCommandResult.Render(CreateReviewResponse(reviewCode, profile));
        }

        if (context.Arguments is
            [
                "buildings", "edit-save", var saveCode, var savePartition,
                var saveTargetPool, var savePoolsRaw, var saveTemplate, var saveMaskRaw, var saveAllCallRaw,
            ])
        {
            var profiles = (await store.LoadEffectiveAsync(
                legacyConfiguration,
                context.CancellationToken)).ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
            var profile = CreateProfile(
                savePartition, saveTargetPool, savePoolsRaw, saveTemplate, saveMaskRaw, saveAllCallRaw);
            EnsurePoolsAreUnambiguous(profiles, saveCode, profile);
            profiles[NormalizeCode(saveCode)] = profile;
            await store.ReplaceAsync(profiles, context.CancellationToken);
            return ModuleCommandResult.Ok($"Saved building profile '{NormalizeCode(saveCode)}'.");
        }

        if (context.Arguments is ["buildings", "delete-review", var deleteReviewCode])
        {
            if (!store.Exists || !(await store.LoadAsync(context.CancellationToken)).ContainsKey(deleteReviewCode))
            {
                throw new InvalidOperationException(
                    $"Local building profile '{deleteReviewCode}' was not found.");
            }
            return ModuleCommandResult.Render(new ModuleTableResponse(
                "Confirm building profile deletion",
                ["BUILDING", "ACTION"],
                [
                    new ModuleTableRow(
                        "confirm",
                        [deleteReviewCode, "Delete this local profile"],
                        ["configure", "buildings", "delete", deleteReviewCode]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
        }

        if (context.Arguments is ["buildings", "delete", var deleteCode])
        {
            var deleted = await store.DeleteAsync(deleteCode, context.CancellationToken);
            return ModuleCommandResult.Ok(deleted
                ? $"Deleted local building profile '{deleteCode}'."
                : $"Local building profile '{deleteCode}' was not found.");
        }

        if (context.Arguments is ["buildings", "import-review"])
        {
            var legacyProfiles = PhoneConfigurationChecks.ParseBuildingPatterns(
                legacyConfiguration ?? "{}");
            if (legacyProfiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "The legacy 'building-patterns' setting has no profiles to import.");
            }
            return ModuleCommandResult.Render(CreateImportReviewResponse(legacyProfiles, store.Exists));
        }

        if (context.Arguments is ["buildings", "import-save"])
        {
            var legacyProfiles = PhoneConfigurationChecks.ParseBuildingPatterns(
                legacyConfiguration ?? "{}");
            if (legacyProfiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "The legacy 'building-patterns' setting has no profiles to import.");
            }
            await store.ReplaceAsync(legacyProfiles, context.CancellationToken);
            return ModuleCommandResult.Ok(
                $"Imported {legacyProfiles.Count} building profiles from the legacy setting.");
        }

        return ModuleCommandResult.Fail(
            "Usage: vt cucm configure [buildings]",
            exitCode: 2);
    }

    internal static string? LoadingStatus(IReadOnlyList<string> arguments) => arguments switch
    {
        ["buildings"] => "Loading building profiles",
        ["buildings", "select", var code] => $"Loading building profile '{code}'",
        ["buildings", "add-code", ..] or ["buildings", "edit-partition", ..] =>
            "Loading CUCM route partitions",
        ["buildings", "edit-device-pool", ..] => "Loading CUCM device pools",
        ["buildings", "edit-recognized", ..] => "Loading recognized device pools",
        ["buildings", "edit-template", ..] => "Loading CUCM phone button templates",
        ["buildings", "edit-mask", var code, ..] => $"Loading external call mask for '{code}'",
        ["buildings", "edit-allcall", var code, ..] => $"Loading All Call number for '{code}'",
        ["buildings", "edit-review", var code, ..] => $"Loading building profile review for '{code}'",
        ["buildings", "delete-review", var code] => $"Loading deletion review for '{code}'",
        ["buildings", "import-review"] => "Loading legacy building profiles",
        _ => null,
    };

    private static async Task<ModuleTableResponse> CreatePartitionSelectorAsync(
        CucmResourceQueryService resourceQueries,
        string code,
        CancellationToken cancellationToken)
    {
        var rows = new List<ModuleTableRow>();
        foreach (var partition in await resourceQueries.ListRoutePartitionsAsync(cancellationToken))
        {
            rows.Add(new ModuleTableRow(
                partition.Uuid ?? $"partition:{partition.Name}",
                [partition.Name, Clean(partition.Description)],
                ["configure", "buildings", "edit-device-pool", code, partition.Name]));
        }
        return new ModuleTableResponse(
            $"Room route partition for {code}",
            ["NAME", "DESCRIPTION"],
            rows);
    }

    private static ModuleTableResponse CreateReviewResponse(string rawCode, BuildingPattern profile)
    {
        var code = NormalizeCode(rawCode);
        return new ModuleTableResponse(
            $"Review building profile '{code}'",
            ["CODE", "ROOM PARTITION", "TARGET POOL", "RECOGNIZED POOLS", "PHONE TEMPLATE", "ROOM MASK", "ALL CALL"],
            [
                new ModuleTableRow(
                    "save",
                    [
                        code,
                        profile.RoutePartitionName,
                        profile.DevicePoolName!,
                        string.Join(", ", profile.DevicePoolNames),
                        profile.PhoneTemplateName!,
                        profile.RoomExternalPhoneNumberMask ?? "<not configured>",
                        profile.AllCallNumber ?? "<not configured>",
                    ],
                    [
                        "configure", "buildings", "edit-save", code,
                        profile.RoutePartitionName, profile.DevicePoolName!,
                        string.Join(",", profile.DevicePoolNames), profile.PhoneTemplateName!,
                        profile.RoomExternalPhoneNumberMask ?? string.Empty,
                        profile.AllCallNumber ?? string.Empty,
                    ]),
            ],
            SubmitMode: ModuleTableSubmitMode.Save);
    }

    private static ModuleTableResponse CreateImportReviewResponse(
        IReadOnlyDictionary<string, BuildingPattern> profiles,
        bool overwritesLocalProfiles)
    {
        var rows = profiles
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ModuleTableRow(
                pair.Key,
                [
                    pair.Key,
                    pair.Value.RoutePartitionName,
                    pair.Value.DevicePoolName ?? "<not configured>",
                    string.Join(", ", pair.Value.DevicePoolNames),
                    pair.Value.PhoneTemplateName ?? "<phone default>",
                ]))
            .ToList();
        rows.Add(new ModuleTableRow(
            "save",
            [
                "Save import",
                overwritesLocalProfiles ? "Replace local profiles" : "Create local profile store",
                string.Empty,
                string.Empty,
                string.Empty,
            ],
            ["configure", "buildings", "import-save"]));
        return new ModuleTableResponse(
            "Review legacy building profile import",
            ["CODE", "ROOM PARTITION", "TARGET POOL", "RECOGNIZED POOLS", "PHONE TEMPLATE"],
            rows,
            SubmitMode: ModuleTableSubmitMode.Save);
    }

    private static BuildingPattern CreateProfile(
        string routePartitionName,
        string targetPool,
        string recognizedPoolsRaw,
        string phoneTemplateName,
        string? roomExternalPhoneNumberMaskRaw = null,
        string? allCallNumberRaw = null)
    {
        var pools = recognizedPoolsRaw
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(targetPool.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BuildingPattern(
            routePartitionName.Trim(),
            pools,
            phoneTemplateName.Trim(),
            targetPool.Trim(),
            string.IsNullOrWhiteSpace(roomExternalPhoneNumberMaskRaw) ? null : roomExternalPhoneNumberMaskRaw.Trim(),
            string.IsNullOrWhiteSpace(allCallNumberRaw) ? null : allCallNumberRaw.Trim());
    }

    private static void EnsurePoolsAreUnambiguous(
        IReadOnlyDictionary<string, BuildingPattern> profiles,
        string rawCode,
        BuildingPattern candidate)
    {
        var code = NormalizeCode(rawCode);
        var candidatePools = candidate.DevicePoolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (otherCode, profile) in profiles)
        {
            if (otherCode.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var overlap = profile.DevicePoolNames.FirstOrDefault(candidatePools.Contains);
            if (overlap is not null)
            {
                throw new InvalidOperationException(
                    $"Device pool '{overlap}' is already assigned to building '{otherCode}'.");
            }
        }
    }

    private static BuildingPattern RequireProfile(
        IReadOnlyDictionary<string, BuildingPattern> profiles,
        string code) =>
        profiles.TryGetValue(code, out var profile)
            ? profile
            : throw new InvalidOperationException($"Building profile '{code}' was not found.");

    private static string NormalizeCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code.Trim().ToUpperInvariant();
    }

    private static string Clean(string? value) =>
        value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
}