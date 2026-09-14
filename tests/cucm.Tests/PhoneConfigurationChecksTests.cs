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

}