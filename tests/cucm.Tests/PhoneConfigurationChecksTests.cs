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
                "devicePools": ["High School"],
                "phoneTemplateName": "HS-UserRoom"
              }
            }
            """);

        var building = Assert.Single(patterns).Value;
        Assert.Equal("Rooms-PT", building.RoutePartitionName);
        Assert.Equal(["High School"], building.DevicePoolNames);
        Assert.Equal("HS-UserRoom", building.PhoneTemplateName);
    }

    [Fact]
    public void RequireClassroomPhoneTemplateRequiresUserAndRoomSlots()
    {
        var policies = PhoneConfigurationChecks.ParseTemplateCompliancePolicies(
            """
            {
              "HS-UserRoom": {
                "slots": [
                  { "index": 1, "kind": "user" },
                  { "index": 3, "kind": "room" }
                ]
              }
            }
            """);

        Assert.Equal(
            "HS-UserRoom",
            PhoneConfigurationChecks.RequireClassroomPhoneTemplate("HS-UserRoom", policies));
        Assert.Throws<InvalidOperationException>(() =>
            PhoneConfigurationChecks.RequireClassroomPhoneTemplate("Other", policies));
    }
}