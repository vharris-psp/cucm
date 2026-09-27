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
            ["phones", "template", "SEP0001"],
            ClassroomWorkflowNavigation.SelectTemplate("SEP0001"));
        Assert.Equal(
            ["phones", "classroom", "SEP0001"],
            ClassroomWorkflowNavigation.Apply("SEP0001"));
        Assert.Equal(
            ["phones", "classroom-user", "SEP0001", "alice"],
            ClassroomWorkflowNavigation.SelectUser("SEP0001", "alice"));
        Assert.Equal(
            ["phones", "classroom-room", "SEP0001", "alice", "HS"],
            ClassroomWorkflowNavigation.SelectBuilding("SEP0001", "alice", "HS"));
        Assert.Equal(
            ["phones", "classroom-scope", "SEP0001", "alice", "HS"],
            ClassroomWorkflowNavigation.SelectScope("SEP0001", "alice", "HS"));
        Assert.Equal(
            ["phones", "classroom-review", "SEP0001", "alice", "HS", "130", "both"],
            ClassroomWorkflowNavigation.ReviewRoom("SEP0001", "alice", "HS", "130", ClassroomApplyScope.Both));
        Assert.Equal(
            ["phones", "classroom-review", "SEP0001", "alice", "HS", "130", "room"],
            ClassroomWorkflowNavigation.ReviewRoom(
                "SEP0001", "alice", "HS", "130", ClassroomApplyScope.RoomOnly));
        Assert.Equal(
            ["phones", "classroom-apply", "SEP0001", "alice", "HS", "130", "both", plan.Fingerprint],
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
                "phone.location",
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
                "user.dn-association",
                "user.association",
                "previous-owner.association",
                "phone.description",
                "inventory.assignment",
            ],
            first.Changes.Select(change => change.Key));
        Assert.All(first.Changes, change => Assert.False(string.IsNullOrWhiteSpace(change.Action)));
    }

    [Fact]
    public void PlannerDerivesDevicePoolFromSelectedLocation()
    {
        var plan = CreatePlan();
        var devicePoolChange = Assert.Single(
            plan.Changes.Where(change => change.Key == "phone.device-pool"));

        Assert.Equal("HS-Classroom", plan.DevicePoolName);
        Assert.Equal("PHS", plan.LocationName);
        Assert.Equal("Old Pool", devicePoolChange.Current);
        Assert.Equal("HS-Classroom", devicePoolChange.Target);
        Assert.Equal("Update", devicePoolChange.Action);
        Assert.Equal("HS-Rooms", plan.RoomLine.RoutePartitionName);
    }

    [Fact]
    public void PlannerBuildsRoomIdentityFromEachSelectedLocation()
    {
        var highSchoolInput = CreateInput();
        var buildingPatterns = new Dictionary<string, BuildingPattern>(
            highSchoolInput.BuildingPatterns,
            StringComparer.OrdinalIgnoreCase)
        {
            ["MS"] = new BuildingPattern(
                "MS-Rooms",
                ["MS-Legacy"],
                "MS-UserRoom",
                "MS-Classroom",
                LocationName: "PMS"),
        };
        var policies = new Dictionary<string, TemplateCompliancePolicy>(
            highSchoolInput.CompliancePolicies,
            StringComparer.OrdinalIgnoreCase)
        {
            ["MS-UserRoom"] = new TemplateCompliancePolicy(
                [
                    new TemplateComplianceSlot(2, TemplateComplianceSlotKind.User),
                    new TemplateComplianceSlot(4, TemplateComplianceSlotKind.Room),
                ]),
        };

        var highSchool = ClassroomPhonePlanner.Create(highSchoolInput with
        {
            BuildingPatterns = buildingPatterns,
            CompliancePolicies = policies,
        });
        var middleSchool = ClassroomPhonePlanner.Create(highSchoolInput with
        {
            BuildingPatterns = buildingPatterns,
            CompliancePolicies = policies,
            BuildingCode = "MS",
        });

        Assert.Equal("HS-Rooms", highSchool.RoomLine.RoutePartitionName);
        Assert.Equal("HS Room 130", highSchool.RoomLine.Label);
        Assert.Equal("HS Room 130", highSchool.RoomLine.Display);
        Assert.Equal("HS Room 130", highSchool.RoomLine.AlertingName);
        Assert.Equal("HS Room 130", highSchool.RoomLine.Description);
        Assert.Equal("MS-Rooms", middleSchool.RoomLine.RoutePartitionName);
        Assert.Equal("MS Room 130", middleSchool.RoomLine.Label);
        Assert.Equal("MS Room 130", middleSchool.RoomLine.Display);
        Assert.Equal("MS Room 130", middleSchool.RoomLine.AlertingName);
        Assert.Equal("MS Room 130", middleSchool.RoomLine.Description);
    }

    [Fact]
    public void PlannerLeavesRoomExternalMaskUnmanagedWhenBuildingHasNoMaskConfigured()
    {
        var input = CreateInput();
        var withoutMask = input with
        {
            BuildingPatterns = new Dictionary<string, BuildingPattern>(
                input.BuildingPatterns, StringComparer.OrdinalIgnoreCase)
            {
                ["HS"] = input.BuildingPatterns["HS"] with { RoomExternalPhoneNumberMask = null },
            },
        };

        var plan = ClassroomPhonePlanner.Create(withoutMask);

        Assert.Null(plan.RoomLine.ExternalPhoneNumberMask);
        Assert.False(plan.RoomLine.UpdateExternalMask);
        var maskChange = Assert.Single(plan.Changes.Where(change => change.Key == "room.external-mask"));
        Assert.Equal("<Not configured for building>", maskChange.Target);
        Assert.Equal("Configure via 'configure buildings'", maskChange.Action);
    }

    [Fact]
    public void PlannerPlansAllCallSpeedDialWhenPolicyAndBuildingBothConfigureIt()
    {
        var input = CreateInput();
        var withSpeedDial = input with
        {
            BuildingPatterns = new Dictionary<string, BuildingPattern>(
                input.BuildingPatterns, StringComparer.OrdinalIgnoreCase)
            {
                ["HS"] = input.BuildingPatterns["HS"] with { AllCallNumber = "5551000" },
            },
            CompliancePolicies = new Dictionary<string, TemplateCompliancePolicy>(
                input.CompliancePolicies, StringComparer.OrdinalIgnoreCase)
            {
                ["HS-UserRoom"] = new TemplateCompliancePolicy(
                    [
                        .. input.CompliancePolicies["HS-UserRoom"].Slots,
                        new TemplateComplianceSlot(6, TemplateComplianceSlotKind.SpeedDial),
                    ]),
            },
        };

        var plan = ClassroomPhonePlanner.Create(withSpeedDial);

        Assert.NotNull(plan.AllCallSpeedDial);
        Assert.Equal(1, plan.AllCallSpeedDial!.BusyLampFieldIndex);
        Assert.Equal("5551000", plan.AllCallSpeedDial.Destination);
        Assert.True(plan.AllCallSpeedDial.Update);
        var speedDialChange = Assert.Single(plan.Changes.Where(change => change.Key == "phone.all-call"));
        Assert.Equal("5551000", speedDialChange.Target);
        Assert.Equal("Update BLF", speedDialChange.Action);
    }

    [Fact]
    public void PlannerMatchesExistingAllCallByBusyLampOrdinalRatherThanPhysicalButton()
    {
        var input = CreateInput();
        var configured = input with
        {
            Phone = input.Phone with
            {
                BusyLampFields = [new CucmPhoneBusyLampField(1, "5551000", "All Call")],
            },
            BuildingPatterns = new Dictionary<string, BuildingPattern>(
                input.BuildingPatterns, StringComparer.OrdinalIgnoreCase)
            {
                ["HS"] = input.BuildingPatterns["HS"] with { AllCallNumber = "5551000" },
            },
            CompliancePolicies = new Dictionary<string, TemplateCompliancePolicy>(
                input.CompliancePolicies, StringComparer.OrdinalIgnoreCase)
            {
                ["HS-UserRoom"] = new TemplateCompliancePolicy(
                    [
                        .. input.CompliancePolicies["HS-UserRoom"].Slots,
                        new TemplateComplianceSlot(6, TemplateComplianceSlotKind.SpeedDial),
                    ]),
            },
        };

        var plan = ClassroomPhonePlanner.Create(configured);

        Assert.NotNull(plan.AllCallSpeedDial);
        Assert.False(plan.AllCallSpeedDial!.Update);
        var speedDialChange = Assert.Single(plan.Changes, change => change.Key == "phone.all-call");
        Assert.Equal("5551000", speedDialChange.Current);
        Assert.Equal("No change", speedDialChange.Action);
    }

    [Fact]
    public void PlannerDoesNotTreatOrdinarySpeedDialAsConfiguredBlfHardkey()
    {
        var input = CreateInput();
        var configured = input with
        {
            Phone = input.Phone with
            {
                SpeedDials = [new CucmPhoneSpeedDial(1, "5551000", "All Call")],
            },
            BuildingPatterns = new Dictionary<string, BuildingPattern>(
                input.BuildingPatterns, StringComparer.OrdinalIgnoreCase)
            {
                ["HS"] = input.BuildingPatterns["HS"] with { AllCallNumber = "5551000" },
            },
            CompliancePolicies = new Dictionary<string, TemplateCompliancePolicy>(
                input.CompliancePolicies, StringComparer.OrdinalIgnoreCase)
            {
                ["HS-UserRoom"] = new TemplateCompliancePolicy(
                    [
                        .. input.CompliancePolicies["HS-UserRoom"].Slots,
                        new TemplateComplianceSlot(6, TemplateComplianceSlotKind.SpeedDial),
                    ]),
            },
        };

        var plan = ClassroomPhonePlanner.Create(configured);

        Assert.NotNull(plan.AllCallSpeedDial);
        Assert.True(plan.AllCallSpeedDial!.Update);
        Assert.Equal([1], plan.AllCallSpeedDial.MisplacedRegularSpeedDialIndexes);
    }

    [Fact]
    public void PlannerLeavesAllCallUnmanagedWhenBuildingHasNoNumberConfigured()
    {
        var input = CreateInput();
        var withSpeedDialSlotOnly = input with
        {
            CompliancePolicies = new Dictionary<string, TemplateCompliancePolicy>(
                input.CompliancePolicies, StringComparer.OrdinalIgnoreCase)
            {
                ["HS-UserRoom"] = new TemplateCompliancePolicy(
                    [
                        .. input.CompliancePolicies["HS-UserRoom"].Slots,
                        new TemplateComplianceSlot(6, TemplateComplianceSlotKind.SpeedDial),
                    ]),
            },
        };

        var plan = ClassroomPhonePlanner.Create(withSpeedDialSlotOnly);

        Assert.Null(plan.AllCallSpeedDial);
        var speedDialChange = Assert.Single(plan.Changes.Where(change => change.Key == "phone.all-call"));
        Assert.Equal("<Not configured for building>", speedDialChange.Target);
        Assert.Equal("Configure via 'configure buildings'", speedDialChange.Action);
    }

    [Fact]
    public async Task SaveSendsAllCallSpeedDialWriteWhenConfigured()
    {
        var input = CreateInput();
        var withSpeedDial = input with
        {
            Phone = input.Phone with
            {
                SpeedDials =
                [
                    new CucmPhoneSpeedDial(1, "5551000", "All Call"),
                    new CucmPhoneSpeedDial(2, "5551000", "All Call"),
                ],
            },
            BuildingPatterns = new Dictionary<string, BuildingPattern>(
                input.BuildingPatterns, StringComparer.OrdinalIgnoreCase)
            {
                ["HS"] = input.BuildingPatterns["HS"] with { AllCallNumber = "5551000" },
            },
            CompliancePolicies = new Dictionary<string, TemplateCompliancePolicy>(
                input.CompliancePolicies, StringComparer.OrdinalIgnoreCase)
            {
                ["HS-UserRoom"] = new TemplateCompliancePolicy(
                    [
                        .. input.CompliancePolicies["HS-UserRoom"].Slots,
                        new TemplateComplianceSlot(6, TemplateComplianceSlotKind.SpeedDial),
                    ]),
            },
        };
        var plan = ClassroomPhonePlanner.Create(withSpeedDial);
        var writer = new RecordingWriter();

        await ClassroomPhoneExecutor.ExecuteAsync(plan, writer);

        Assert.Contains("all-call-speed-dial", writer.Calls);
        Assert.Contains("remove-misplaced-all-call-speed-dial-1", writer.Calls);
        Assert.Contains("remove-misplaced-all-call-speed-dial-2", writer.Calls);
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
    public void RoomOnlyScopeExcludesEveryUserLineOperationAndAssociation()
    {
        var plan = ClassroomPhonePlanner.Create(
            CreateInput() with { Scope = ClassroomApplyScope.RoomOnly });

        Assert.False(plan.UserLine.CreateDirectoryNumber);
        Assert.False(plan.UserLine.AssignLine);
        Assert.False(plan.UserLine.UpdateDirectoryNumber);
        Assert.False(plan.UserLine.UpdateDisplay);
        Assert.False(plan.UserLine.UpdateLabel);
        Assert.False(plan.UserLine.UpdateExternalMask);
        Assert.False(plan.AddUserAssociation);
        Assert.False(plan.RemovePreviousOwnerAssociation);
        Assert.False(plan.RecordLocalAssignment);
        Assert.Null(plan.PreviousOwnerUserId);
        Assert.True(plan.RoomLine.CreateDirectoryNumber);
        Assert.True(plan.RoomLine.AssignLine);

        var userDnChange = Assert.Single(plan.Changes.Where(change => change.Key == "user.dn"));
        Assert.Equal("<Not applied>", userDnChange.Target);
        Assert.Equal("Skip (excluded)", userDnChange.Action);
    }

    [Fact]
    public void UserOnlyScopeExcludesEveryRoomLineOperation()
    {
        var plan = ClassroomPhonePlanner.Create(
            CreateInput() with { Scope = ClassroomApplyScope.UserOnly });

        Assert.False(plan.RoomLine.CreateDirectoryNumber);
        Assert.False(plan.RoomLine.AssignLine);
        Assert.False(plan.RoomLine.UpdateDirectoryNumber);
        Assert.False(plan.RoomLine.UpdateDisplay);
        Assert.False(plan.RoomLine.UpdateLabel);
        Assert.False(plan.RoomLine.UpdateExternalMask);
        Assert.True(plan.UserLine.CreateDirectoryNumber);
        Assert.True(plan.UserLine.AssignLine);
        Assert.True(plan.AddUserAssociation);

        var roomDnChange = Assert.Single(plan.Changes.Where(change => change.Key == "room.dn"));
        Assert.Equal("<Not applied>", roomDnChange.Target);
        Assert.Equal("Skip (excluded)", roomDnChange.Action);
    }

    [Fact]
    public void UserDnNoAnswerForwardsToVoiceMailWithoutChangingOtherForwardTargets()
    {
        var plan = ClassroomPhonePlanner.Create(CreateInput() with
        {
            UserForwardCallingSearchSpaceName = "Subscribe",
            UserNoAnswerRingDurationSeconds = 20,
        });

        Assert.True(plan.UserLine.ForwardNoAnswerToVoiceMail);
        Assert.False(plan.RoomLine.ForwardNoAnswerToVoiceMail);

        var userForwards = CucmClassroomPhoneWriter.BuildForwardSettings(plan.UserLine);
        Assert.False(userForwards.All!.ForwardToVoiceMail);
        Assert.False(userForwards.Busy!.ForwardToVoiceMail);
        Assert.True(userForwards.NoAnswer!.ForwardToVoiceMail);
        Assert.True(userForwards.NoAnswerInternal!.ForwardToVoiceMail);
        Assert.Equal(20, userForwards.NoAnswer.NoAnswerRingDurationSeconds);
        Assert.False(userForwards.NoCoverage!.ForwardToVoiceMail);
        Assert.False(userForwards.NotRegistered!.ForwardToVoiceMail);
    }

    [Fact]
    public void PlannerTreatsUserDnNoAnswerVoiceMailPolicyAsConverged()
    {
        var forwarding = new CucmCallForwardSettings(
            ForwardToVoiceMail: false, CallingSearchSpaceName: "Subscribe");
        var noAnswer = forwarding with
        {
            ForwardToVoiceMail = true,
            NoAnswerRingDurationSeconds = 20,
        };
        var userDirectoryNumber = new CucmDirectoryNumber(
            "user-dn-uuid", "1234", "User DID 1234", null, "Users-PT", "Users-CSS", "Users-VM",
            forwarding, "Alice Example",
            CallForwardBusy: forwarding,
            CallForwardBusyInternal: forwarding,
            CallForwardNoAnswer: noAnswer,
            CallForwardNoAnswerInternal: noAnswer,
            CallForwardNoCoverage: forwarding,
            CallForwardNoCoverageInternal: forwarding,
            CallForwardOnFailure: forwarding,
            CallForwardNotRegistered: forwarding,
            CallForwardNotRegisteredInternal: forwarding);

        var plan = ClassroomPhonePlanner.Create(CreateInput() with
        {
            UserDirectoryNumber = userDirectoryNumber,
            UserForwardCallingSearchSpaceName = "Subscribe",
            UserNoAnswerRingDurationSeconds = 20,
        });

        Assert.False(plan.UserLine.UpdateDirectoryNumber);
    }

    [Fact]
    public void PartialScopeDoesNotRequireTheResultingPhoneToBeFullyCompliant()
    {
        // With an empty phone and only the room slot included, the user slot stays unfilled —
        // which would fail the full "Both" compliance check but must not block a partial apply.
        var plan = ClassroomPhonePlanner.Create(
            CreateInput() with { Scope = ClassroomApplyScope.RoomOnly });

        Assert.Equal(ClassroomApplyScope.RoomOnly, plan.Scope);
    }

    [Fact]
    public async Task SaveOnlySendsRoomOperationsWhenScopeIsRoomOnly()
    {
        var writer = new RecordingWriter();
        var plan = ClassroomPhonePlanner.Create(
            CreateInput() with { Scope = ClassroomApplyScope.RoomOnly });

        var result = await ClassroomPhoneExecutor.ExecuteAsync(plan, writer);

        Assert.DoesNotContain(writer.Calls, call => call.Contains("User", StringComparison.Ordinal));
        Assert.DoesNotContain(writer.Calls, call => call == "selected-user-association");
        Assert.DoesNotContain(writer.Calls, call => call == "local-assignment");
        Assert.Contains("create-Room", writer.Calls);
        Assert.Contains("assign-Room", writer.Calls);
        Assert.NotEmpty(result.CompletedOperations);
    }

    [Fact]
    public void PlannerRejectsADisplayValueThatExceedsCucmsThirtyCharacterLimit()
    {
        var input = CreateInput() with
        {
            UserTemplate = CreateInput().UserTemplate with
            {
                Display = "Public Schools of Petoskey:{userDisplayName}",
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ClassroomPhonePlanner.Create(input));

        Assert.Contains("30-character limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'display'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerRejectsALabelValueContainingACharacterCucmForbids()
    {
        var input = CreateInput() with
        {
            UserTemplate = CreateInput().UserTemplate with { Label = "{userDisplayName} <Line>" },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ClassroomPhonePlanner.Create(input));

        Assert.Contains("'label'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not allow", exception.Message, StringComparison.Ordinal);
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
                "phone-configuration-refresh",
            ],
            writer.Calls);
        Assert.Equal(18, result.CompletedOperations.Count);
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
                LocationName = initial.LocationName,
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
        Assert.Contains("Synthetic write failure.", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
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
            "Old-Template",
            LocationName: "Old Location");
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
            ["HS"] = new BuildingPattern(
                "HS-Rooms",
                ["HS-Legacy"],
                "HS-UserRoom",
                "HS-Classroom",
                RoomExternalPhoneNumberMask: "555{room}",
                LocationName: "PHS"),
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

        public Task UpdateAllCallSpeedDialAsync(
            string phoneName,
            ClassroomSpeedDialPlan speedDial,
            CancellationToken cancellationToken) => Record("all-call-speed-dial");

        public Task RemoveRegularSpeedDialAsync(
            string phoneName,
            int speedDialIndex,
            CancellationToken cancellationToken) =>
            Record($"remove-misplaced-all-call-speed-dial-{speedDialIndex}");

        public Task UpdateUserAssociationAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("selected-user-association");

        public Task UpdatePreviousOwnerAssociationAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("previous-owner-association");

        public Task UpdateDescriptionAsync(
            ClassroomPhonePlan plan,
            CancellationToken cancellationToken) => Record("phone-description");

        public Task ApplyPhoneConfigurationAsync(
            string phoneName,
            CancellationToken cancellationToken) => Record("phone-configuration-refresh");

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