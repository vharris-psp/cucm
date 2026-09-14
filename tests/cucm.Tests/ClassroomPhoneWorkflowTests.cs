using VSharp.Cucm.Models;

public sealed class ClassroomPhoneWorkflowTests
{
    [Fact]
    public void ConfigurationRequiresExplicitClassroomTemplateNames()
    {
        var configuration = new Dictionary<string, string>
        {
            ["classroom-room-line-template"] = " PHS Classroom ",
            ["classroom-user-line-template"] = "UserDN",
        };

        Assert.Equal(
            "PHS Classroom",
            ClassroomConfiguration.RequireValue(configuration, "classroom-room-line-template"));
        Assert.Equal(
            "UserDN",
            ClassroomConfiguration.RequireValue(configuration, "classroom-user-line-template"));
        Assert.Throws<InvalidOperationException>(() =>
            ClassroomConfiguration.RequireValue(configuration, "missing-template"));
    }

    [Fact]
    public void NavigationSelectsPhoneThenUserThenRoomAndCarriesReviewFingerprint()
    {
        var plan = CreatePlan();

        Assert.Equal(
            ["phones", "classroom-user", "SEP0001", "alice"],
            ClassroomWorkflowNavigation.SelectUser("SEP0001", "alice"));
        Assert.Equal(
            ["phones", "classroom-room", "SEP0001", "alice", "HS"],
            ClassroomWorkflowNavigation.SelectBuilding("SEP0001", "alice", "HS"));
        Assert.Equal(
            ["phones", "classroom-review", "SEP0001", "alice", "HS"],
            ClassroomWorkflowNavigation.ReviewRoom("SEP0001", "alice", "HS"));
        Assert.Equal(
            ["phones", "classroom-apply", "SEP0001", "alice", "HS", "130", plan.Fingerprint],
            ClassroomWorkflowNavigation.Save(plan));
    }

    [Fact]
    public void PlannerUsesPolicySlotsAndReviewsEveryManagedFieldDeterministically()
    {
        var first = CreatePlan();
        var second = CreatePlan();

        Assert.Equal(2, first.UserLine.Index);
        Assert.Equal(4, first.RoomLine.Index);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            [
                "phone.device-pool",
                "phone.button-template",
                "room.dn",
                "room.partition",
                "room.description",
                "room.alerting-name",
                "room.caller-id",
                "room.label",
                "room.external-mask",
                "room.voicemail",
                "user.dn",
                "user.partition",
                "user.description",
                "user.css",
                "user.alerting-name",
                "user.caller-id",
                "user.label",
                "user.external-mask",
                "user.voicemail",
                "user.owner",
                "user.association",
                "previous-owner.association",
                "phone.description",
                "inventory.assignment",
            ],
            first.Changes.Select(change => change.Key));
        Assert.All(first.Changes, change => Assert.False(string.IsNullOrWhiteSpace(change.Action)));
    }

    [Fact]
    public void PlannerRejectsIncompleteClassroomConfiguration()
    {
        var input = CreateInput() with
        {
            UserTemplate = CreateInput().UserTemplate with { ExternalPhoneNumberMask = null },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ClassroomPhonePlanner.Create(input));

        Assert.Contains("externalPhoneNumberMask", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveExecutesRequiredOperationsInDependencyOrder()
    {
        var writer = new RecordingWriter();

        var result = await ClassroomPhoneExecutor.ExecuteAsync(CreatePlan(), writer);

        Assert.Equal(
            [
                "phone-profile",
                "create-Room",
                "assign-Room",
                "dn-options-Room",
                "caller-id-Room",
                "label-Room",
                "external-mask-Room",
                "create-User",
                "selected-user-association",
                "previous-owner-association",
                "assign-User",
                "dn-options-User",
                "caller-id-User",
                "label-User",
                "external-mask-User",
                "phone-description",
                "local-assignment",
            ],
            writer.Calls);
        Assert.Equal(17, result.CompletedOperations.Count);
    }

    [Fact]
    public async Task SaveSkipsAllWritesWhenReviewedStateIsAlreadyConverged()
    {
        var initial = CreatePlan();
        var convergedInput = CreateInput() with
        {
            Phone = CreateInput().Phone with
            {
                Description = initial.Description,
                DevicePoolName = initial.DevicePoolName,
                PhoneTemplateName = initial.PhoneTemplateName,
                OwnerUserName = initial.UserId,
                Lines =
                [
                    ToAppearance(initial.UserLine),
                    ToAppearance(initial.RoomLine),
                ],
            },
            User = CreateInput().User with
            {
                AssociatedDevices = ["SEP0001", "SEPOTHER"],
            },
            UserDid = CreateInput().UserDid with
            {
                Assignment = new UserDidAssignment(
                    "SEP0001",
                    initial.UserLine.Index,
                    "alice",
                    initial.UserLine.RoutePartitionName,
                    DateTimeOffset.UnixEpoch),
            },
            UserDirectoryNumber = ToDirectoryNumber(initial.UserLine),
            RoomDirectoryNumber = ToDirectoryNumber(initial.RoomLine),
            PreviousOwner = null,
        };
        var converged = ClassroomPhonePlanner.Create(convergedInput);
        var writer = new RecordingWriter();

        var result = await ClassroomPhoneExecutor.ExecuteAsync(converged, writer);

        Assert.Empty(writer.Calls);
        Assert.Empty(result.CompletedOperations);
        Assert.All(converged.Changes, change => Assert.Equal("No change", change.Action));
    }

    [Fact]
    public async Task SaveReportsCompletedOperationsAndStopsAfterPartialFailure()
    {
        var writer = new RecordingWriter("label-Room");

        var exception = await Assert.ThrowsAsync<ClassroomPhoneApplyException>(() =>
            ClassroomPhoneExecutor.ExecuteAsync(CreatePlan(), writer));

        Assert.Equal("room-line-4-label", exception.FailedOperation);
        Assert.Equal(
            [
                "phone-profile",
                "room-line-4-create-dn",
                "room-line-4-assign",
                "room-line-4-dn-options",
                "room-line-4-caller-id",
            ],
            exception.CompletedOperations);
        Assert.Equal(
            [
                "phone-profile",
                "create-Room",
                "assign-Room",
                "dn-options-Room",
                "caller-id-Room",
                "label-Room",
            ],
            writer.Calls);
        Assert.Contains("No automatic rollback", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerDoesNotRecordInventoryAssignmentForUsersAssignedDn()
    {
        var plan = ClassroomPhonePlanner.Create(CreateInput() with
        {
            UserDidUsesInventoryFallback = false,
        });

        Assert.False(plan.RecordLocalAssignment);
        var inventoryChange = Assert.Single(
            plan.Changes.Where(change => change.Key == "inventory.assignment"));
        Assert.Equal("Not used", inventoryChange.Current);
        Assert.Equal("Not used", inventoryChange.Target);
        Assert.Equal("No change", inventoryChange.Action);
    }

    private static ClassroomPhonePlan CreatePlan() =>
        ClassroomPhonePlanner.Create(CreateInput());

    private static ClassroomPhonePlanInput CreateInput()
    {
        var phone = new CucmPhone(
            "phone-uuid",
            "SEP0001",
            "Legacy description",
            "Cisco 8861",
            "Cisco 8861",
            "SIP",
            "old-owner",
            [],
            "Old Pool",
            "Old-Template");
        var user = new CucmUser(
            "user-uuid",
            "alice",
            "Alice",
            null,
            "Example",
            "Alice Example",
            null,
            null,
            "1234",
            null,
            ["SEPOTHER"]);
        var previousOwner = new CucmUser(
            "previous-user-uuid",
            "old-owner",
            null,
            null,
            null,
            "Previous Owner",
            null,
            null,
            null,
            null,
            ["SEP0001", "SEPOtherOldPhone"]);
        var buildingPatterns = new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase)
        {
            ["HS"] = new BuildingPattern("HS-Rooms", ["HS-Classroom"], "HS-UserRoom"),
        };
        var policies = new Dictionary<string, TemplateCompliancePolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["HS-UserRoom"] = new TemplateCompliancePolicy(
                [
                    new TemplateComplianceSlot(2, TemplateComplianceSlotKind.User),
                    new TemplateComplianceSlot(4, TemplateComplianceSlotKind.Room),
                ]),
        };
        var roomTemplate = new LineTemplate(
            LineTemplateKind.Room,
            null,
            "Room {room}",
            "{building} Room {room}",
            "Room {room}",
            "555{room}",
            false,
            "Rooms-VM");
        var userTemplate = new LineTemplate(
            LineTemplateKind.User,
            null,
            "{userDisplayName}",
            "{userDisplayName}",
            "{userDisplayName}",
            "555{pattern}",
            true,
            "Users-VM");

        return new ClassroomPhonePlanInput(
            phone,
            user,
            new UserDid("1234", "Users-PT", "User DID 1234", "Users-CSS", "Users-VM"),
            UserDidUsesInventoryFallback: true,
            UserDirectoryNumber: null,
            RoomDirectoryNumber: null,
            previousOwner,
            buildingPatterns,
            policies,
            "HS",
            "130",
            roomTemplate,
            userTemplate);
    }

    private static CucmPhoneLineAppearance ToAppearance(ClassroomLinePlan line) =>
        new(
            line.Index,
            line.Pattern,
            line.RoutePartitionName,
            line.Label,
            line.Display,
            line.Display,
            line.ExternalPhoneNumberMask);

    private static CucmDirectoryNumber ToDirectoryNumber(ClassroomLinePlan line) =>
        new(
            $"{line.Kind}-uuid",
            line.Pattern,
            line.Description,
            "Device",
            line.RoutePartitionName,
            line.CallingSearchSpaceName,
            line.VoiceMailProfileName,
            new CucmCallForwardSettings(),
            line.AlertingName);

    private sealed class RecordingWriter(string? failAt = null) : IClassroomPhoneWriter
    {
        internal List<string> Calls { get; } = [];

        public Task UpdatePhoneProfileAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("phone-profile");

        public Task CreateDirectoryNumberAsync(
            ClassroomLinePlan line,
            CancellationToken cancellationToken) => Record($"create-{line.Kind}");

        public Task AssignLineAsync(
            string phoneName,
            ClassroomLinePlan line,
            CancellationToken cancellationToken) => Record($"assign-{line.Kind}");

        public Task UpdateDirectoryNumberAsync(
            ClassroomLinePlan line,
            CancellationToken cancellationToken) => Record($"dn-options-{line.Kind}");

        public Task UpdateLineDisplayAsync(
            string phoneName,
            ClassroomLinePlan line,
            CancellationToken cancellationToken) => Record($"caller-id-{line.Kind}");

        public Task UpdateLineLabelAsync(
            string phoneName,
            ClassroomLinePlan line,
            CancellationToken cancellationToken) => Record($"label-{line.Kind}");

        public Task UpdateLineExternalMaskAsync(
            string phoneName,
            ClassroomLinePlan line,
            CancellationToken cancellationToken) => Record($"external-mask-{line.Kind}");

        public Task UpdateUserAssociationAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("selected-user-association");

        public Task UpdatePreviousOwnerAssociationAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("previous-owner-association");

        public Task UpdateDescriptionAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("phone-description");

        public Task RecordLocalAssignmentAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("local-assignment");

        private Task Record(string operation)
        {
            Calls.Add(operation);
            return operation == failAt
                ? Task.FromException(new InvalidOperationException("Synthetic write failure."))
                : Task.CompletedTask;
        }
    }
}