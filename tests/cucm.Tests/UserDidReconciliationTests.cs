public sealed class UserDidReconciliationTests
{
    private static VSharp.Cucm.Models.CucmDirectoryNumber Line(
        string pattern,
        string partition,
        string usage = "Device") =>
        new(
            Uuid: null,
            Pattern: pattern,
            Description: null,
            Usage: usage,
            RoutePartitionName: partition,
            CallingSearchSpaceName: null,
            VoiceMailProfileName: null,
            CallForwardAll: new VSharp.Cucm.Models.CucmCallForwardSettings());

    [Fact]
    public void UnassignedTrackedDidNotFoundInCucmIsAvailable()
    {
        var result = UserDidReconciler.Resolve(
            [new UserDid("1000", "Users-PT", null, null, null)],
            []);

        var entry = Assert.Single(result.Tracked);
        Assert.Equal(UserDidLiveStatus.Available, entry.Status);
        Assert.Empty(result.Untracked);
    }

    [Fact]
    public void TrackedDidFoundAsDeviceUsageInCucmIsReserved()
    {
        var result = UserDidReconciler.Resolve(
            [new UserDid("1000", "Users-PT", null, null, null)],
            [Line("1000", "Users-PT")]);

        var entry = Assert.Single(result.Tracked);
        Assert.Equal(UserDidLiveStatus.Reserved, entry.Status);
        Assert.Contains("already in use", entry.Reason);
    }

    [Fact]
    public void TrackedDidFoundWithNonDeviceUsageIsReservedByFunction()
    {
        var result = UserDidReconciler.Resolve(
            [new UserDid("1000", "Users-PT", null, null, null)],
            [Line("1000", "Users-PT", usage: "Call Park")]);

        var entry = Assert.Single(result.Tracked);
        Assert.Equal(UserDidLiveStatus.Reserved, entry.Status);
        Assert.Contains("Call Park", entry.Reason);
    }

    [Fact]
    public void TrackedDidWithMatchingLocalAssignmentIsReservedWithAssignmentDetail()
    {
        var did = new UserDid(
            "1000",
            "Users-PT",
            null,
            null,
            null,
            new UserDidAssignment("SEP001", 1, "alice", "Users-PT", DateTimeOffset.UnixEpoch));

        var result = UserDidReconciler.Resolve([did], [Line("1000", "Users-PT")]);

        var entry = Assert.Single(result.Tracked);
        Assert.Equal(UserDidLiveStatus.Reserved, entry.Status);
        Assert.Contains("SEP001", entry.Reason);
        Assert.Contains("alice", entry.Reason);
    }

    [Fact]
    public void TrackedDidWithStaleLocalAssignmentNotFoundInCucmIsAnomaly()
    {
        var did = new UserDid(
            "1000",
            "Users-PT",
            null,
            null,
            null,
            new UserDidAssignment("SEP001", 1, "alice", "Users-PT", DateTimeOffset.UnixEpoch));

        var result = UserDidReconciler.Resolve([did], []);

        var entry = Assert.Single(result.Tracked);
        Assert.Equal(UserDidLiveStatus.Anomaly, entry.Status);
        Assert.Contains("SEP001", entry.Reason);
    }

    [Fact]
    public void CucmLineNotMatchingAnyTrackedDidIsAnUntrackedAnomaly()
    {
        var result = UserDidReconciler.Resolve(
            [new UserDid("1000", "Users-PT", null, null, null)],
            [Line("1000", "Users-PT"), Line("9999", "Users-PT")]);

        var anomaly = Assert.Single(result.Untracked);
        Assert.Equal("9999", anomaly.Pattern);
        Assert.Contains("not present in the tracked local DID inventory", anomaly.Reason);
    }

    [Fact]
    public void AReservedExtensionIsExcludedFromTheUntrackedAnomalyList()
    {
        var result = UserDidReconciler.Resolve(
            [new UserDid("1000", "Users-PT", null, null, null)],
            [Line("1000", "Users-PT"), Line("9999", "Users-PT")],
            [new ReservedExtension("9999", "Users-PT", "Known internal extension.", DateTimeOffset.UnixEpoch)]);

        Assert.Empty(result.Untracked);
    }

    [Fact]
    public void ParsePartitionsSplitsTrimsAndDeduplicates()
    {
        var partitions = UserDidReconciler.ParsePartitions(" AllPhones ,AllPhones; Rooms-PT ");

        Assert.Equal(["AllPhones", "Rooms-PT"], partitions);
    }

    [Fact]
    public void ParsePartitionsReturnsEmptyForBlankValue()
    {
        Assert.Empty(UserDidReconciler.ParsePartitions("  "));
        Assert.Empty(UserDidReconciler.ParsePartitions(null));
    }
}
