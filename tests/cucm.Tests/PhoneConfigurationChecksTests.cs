public sealed class PhoneConfigurationChecksTests
{
    [Fact]
    public void ParseBuildingPatternsReadsPhoneTemplateName()
    {
        var patterns = PhoneConfigurationChecks.ParseBuildingPatterns(
            """
            {
              "HS": {
                "routePartitionName": "Rooms-PT",
                "devicePoolName": "High School Classrooms",
                "devicePools": ["High School"],
                "phoneTemplateName": "HS-UserRoom",
                "locationName": "PHS"
              }
            }
            """);

        var building = Assert.Single(patterns).Value;
        Assert.Equal("Rooms-PT", building.RoutePartitionName);
        Assert.Equal("High School Classrooms", building.DevicePoolName);
        Assert.Equal(["High School", "High School Classrooms"], building.DevicePoolNames);
        Assert.Equal("HS-UserRoom", building.PhoneTemplateName);
        Assert.Equal("PHS", building.LocationName);
    }

    [Fact]
    public void ParseBuildingPatternsAcceptsCommaSeparatedDevicePoolsString()
    {
        // A JSON array of devicePools crashes 'vt module configure's markup renderer on this
        // setting's raw value (literal '[' ']' are parsed as style tags) - a bracket-free
        // comma-separated string must also be accepted.
        var patterns = PhoneConfigurationChecks.ParseBuildingPatterns(
            """
            {
              "HS": {
                "routePartitionName": "Rooms-PT",
                "devicePoolName": "High School Classrooms",
                "devicePools": "High School, High School Classrooms",
                "phoneTemplateName": "HS-UserRoom"
              }
            }
            """);

        var building = Assert.Single(patterns).Value;
        Assert.Equal(["High School", "High School Classrooms"], building.DevicePoolNames);
    }

    [Fact]
    public void ParseTemplateCompliancePoliciesAcceptsCommaSeparatedSlotsString()
    {
        // A JSON 'slots' array crashes 'vt module configure's markup renderer on this setting's
        // raw value (literal '[' ']' are parsed as style tags) - a bracket-free "index:kind,..."
        // string must also be accepted.
        var policies = PhoneConfigurationChecks.ParseTemplateCompliancePolicies(
            """
            {
              "Standard 7841 SIP 1DN-1SdBLF-2DN": { "slots": "1:user,3:room" }
            }
            """);

        var policy = Assert.Single(policies).Value;
        Assert.Equal(
            [
                new TemplateComplianceSlot(1, TemplateComplianceSlotKind.User),
                new TemplateComplianceSlot(3, TemplateComplianceSlotKind.Room),
            ],
            policy.Slots);
    }

    [Fact]
    public void EvaluateClassroomUsesOwnerDnAndBuildingRoutingInsteadOfPlaceholderValues()
    {
        var phone = new VSharp.Cucm.Models.CucmPhone(
            "phone-uuid",
            "SEP0001",
            null,
            "Cisco 7841",
            "Cisco 7841",
            "SIP",
            "vharris",
            [
                new VSharp.Cucm.Models.CucmPhoneLineAppearance(
                    1, "2112", "AllPhones", "Victor Harris", "Victor Harris", "Victor Harris"),
                new VSharp.Cucm.Models.CucmPhoneLineAppearance(
                    3, "130", "HS-Rooms", "PHS Room 130", "PHS Room 130", "PHS Room 130"),
            ],
            "HighSchool",
            "Standard 7841 SIP 1DN-1SdBLF-2DN",
            LocationName: "PHS");
        var owner = new VSharp.Cucm.Models.CucmUser(
            "user-uuid",
            "vharris",
            "Victor",
            null,
            "Harris",
            "Victor Harris",
            null,
            null,
            "2112",
            null,
            ["SEP0001"],
            new VSharp.Cucm.Models.CucmUserPrimaryExtension("2112", "AllPhones"));
        var policies = new Dictionary<string, TemplateCompliancePolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard 7841 SIP 1DN-1SdBLF-2DN"] = new TemplateCompliancePolicy(
                [
                    new TemplateComplianceSlot(1, TemplateComplianceSlotKind.User),
                    new TemplateComplianceSlot(3, TemplateComplianceSlotKind.Room),
                ]),
        };
        var buildings = new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase)
        {
            ["PHS"] = new BuildingPattern(
                "HS-Rooms",
                ["HighSchool"],
                "Standard 7841 SIP 1DN-1SdBLF-2DN",
                "HighSchool",
                LocationName: "PHS"),
        };

        var results = PhoneConfigurationChecks.EvaluateClassroom(
            phone,
            owner,
            policies,
            buildings);

        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.Equal(PhoneCheckStatus.Passed, result.Status));
        Assert.Equal("2112", results[0].Expected);
        Assert.Equal("2112", results[0].Actual);
        Assert.Equal("PHS", results[1].Actual);
        Assert.Equal("PHS", results[2].Actual);
        Assert.Equal("130", results[3].Actual);
        Assert.Equal("HS-Rooms", results[4].Actual);
    }

    [Fact]
    public void EvaluateClassroomPassesWhenForwardPickupAndVoicemailMatchExpectations()
    {
        var (phone, owner, policies, buildings) = CreateClassroomFixture();
        var forwardSettings = new VSharp.Cucm.Models.CucmCallForwardSettings(
            ForwardToVoiceMail: false, CallingSearchSpaceName: "Subscribe");
        var noAnswerSettings = forwardSettings with
        {
            ForwardToVoiceMail = true,
            NoAnswerRingDurationSeconds = 20,
        };
        var userDn = new VSharp.Cucm.Models.CucmDirectoryNumber(
            "dn-uuid", "2112", null, null, "AllPhones", null, "VM-Profile", forwardSettings,
            CallForwardBusy: forwardSettings,
            CallForwardBusyInternal: forwardSettings,
            CallForwardNoAnswer: noAnswerSettings,
            CallForwardNoAnswerInternal: noAnswerSettings,
            CallForwardNoCoverage: forwardSettings,
            CallForwardNoCoverageInternal: forwardSettings,
            CallForwardOnFailure: forwardSettings,
            CallForwardNotRegistered: forwardSettings,
            CallForwardNotRegisteredInternal: forwardSettings,
            CallingSearchSpaceActivationPolicy: "Use System Default",
            CallPickupGroupName: null);

        var results = PhoneConfigurationChecks.EvaluateClassroom(
            phone,
            owner,
            policies,
            buildings,
            userDn,
            expectedForwardCallingSearchSpaceName: "Subscribe",
            expectedCallingSearchSpaceActivationPolicy: "Use System Default",
            expectedNoAnswerRingDurationSeconds: 20,
            availableVoiceMailProfiles: ["VM-Profile"]);

        Assert.All(results, result => Assert.Equal(PhoneCheckStatus.Passed, result.Status));
    }

    [Fact]
    public void EvaluateClassroomFailsWhenPickupGroupSetOrVoicemailUnknownOrForwardsToVoicemail()
    {
        var (phone, owner, policies, buildings) = CreateClassroomFixture();
        var badForward = new VSharp.Cucm.Models.CucmCallForwardSettings(
            ForwardToVoiceMail: true, CallingSearchSpaceName: "WrongCss");
        var userDn = new VSharp.Cucm.Models.CucmDirectoryNumber(
            "dn-uuid", "2112", null, null, "AllPhones", null, "Unknown-Profile", badForward,
            CallForwardBusy: badForward,
            CallingSearchSpaceActivationPolicy: "Custom",
            CallPickupGroupName: "PickupGroup1");

        var results = PhoneConfigurationChecks.EvaluateClassroom(
            phone,
            owner,
            policies,
            buildings,
            userDn,
            expectedForwardCallingSearchSpaceName: "Subscribe",
            expectedCallingSearchSpaceActivationPolicy: "Use System Default",
            expectedNoAnswerRingDurationSeconds: 20,
            availableVoiceMailProfiles: ["VM-Profile"]);

        Assert.Contains(results, r => r.Name == "User DN voicemail profile" && r.Status == PhoneCheckStatus.Failed);
        Assert.Contains(results, r => r.Name == "User DN call pickup group" && r.Status == PhoneCheckStatus.Failed);
        Assert.Contains(
            results, r => r.Name == "User DN CSS activation policy" && r.Status == PhoneCheckStatus.Failed);
        Assert.Contains(results, r => r.Name == "User DN Forward All CSS" && r.Status == PhoneCheckStatus.Failed);
        Assert.Contains(results, r => r.Name == "User DN Forward All target" && r.Status == PhoneCheckStatus.Failed);
        Assert.Contains(
            results, r => r.Name == "User DN Forward Busy target" && r.Status == PhoneCheckStatus.Failed);
    }

    private static (
        VSharp.Cucm.Models.CucmPhone Phone,
        VSharp.Cucm.Models.CucmUser Owner,
        Dictionary<string, TemplateCompliancePolicy> Policies,
        Dictionary<string, BuildingPattern> Buildings) CreateClassroomFixture()
    {
        var phone = new VSharp.Cucm.Models.CucmPhone(
            "phone-uuid",
            "SEP0001",
            null,
            "Cisco 7841",
            "Cisco 7841",
            "SIP",
            "vharris",
            [
                new VSharp.Cucm.Models.CucmPhoneLineAppearance(
                    1, "2112", "AllPhones", "Victor Harris", "Victor Harris", "Victor Harris"),
                new VSharp.Cucm.Models.CucmPhoneLineAppearance(
                    3, "130", "HS-Rooms", "PHS Room 130", "PHS Room 130", "PHS Room 130"),
            ],
            "HighSchool",
            "Standard 7841 SIP 1DN-1SdBLF-2DN",
            LocationName: "PHS");
        var owner = new VSharp.Cucm.Models.CucmUser(
            "user-uuid",
            "vharris",
            "Victor",
            null,
            "Harris",
            "Victor Harris",
            null,
            null,
            "2112",
            null,
            ["SEP0001"],
            new VSharp.Cucm.Models.CucmUserPrimaryExtension("2112", "AllPhones"));
        var policies = new Dictionary<string, TemplateCompliancePolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard 7841 SIP 1DN-1SdBLF-2DN"] = new TemplateCompliancePolicy(
                [
                    new TemplateComplianceSlot(1, TemplateComplianceSlotKind.User),
                    new TemplateComplianceSlot(3, TemplateComplianceSlotKind.Room),
                ]),
        };
        var buildings = new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase)
        {
            ["PHS"] = new BuildingPattern(
                "HS-Rooms",
                ["HighSchool"],
                "Standard 7841 SIP 1DN-1SdBLF-2DN",
                "HighSchool",
                LocationName: "PHS"),
        };
        return (phone, owner, policies, buildings);
    }

}