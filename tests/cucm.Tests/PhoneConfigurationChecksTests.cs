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
                "phoneTemplateName": "HS-UserRoom"
              }
            }
            """);

        var building = Assert.Single(patterns).Value;
        Assert.Equal("Rooms-PT", building.RoutePartitionName);
        Assert.Equal("High School Classrooms", building.DevicePoolName);
        Assert.Equal(["High School", "High School Classrooms"], building.DevicePoolNames);
        Assert.Equal("HS-UserRoom", building.PhoneTemplateName);
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
            "Standard 7841 SIP 1DN-1SdBLF-2DN");
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
                "HighSchool"),
        };

        var results = PhoneConfigurationChecks.EvaluateClassroom(
            phone,
            owner,
            policies,
            buildings);

        Assert.Equal(4, results.Count);
        Assert.All(results, result => Assert.Equal(PhoneCheckStatus.Passed, result.Status));
        Assert.Equal("2112", results[0].Expected);
        Assert.Equal("2112", results[0].Actual);
        Assert.Equal("PHS", results[1].Actual);
        Assert.Equal("130", results[2].Actual);
        Assert.Equal("HS-Rooms", results[3].Actual);
    }

}