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

}