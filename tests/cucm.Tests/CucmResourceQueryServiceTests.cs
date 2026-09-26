using System.Net;
using VSharp.Cucm;

public sealed class CucmResourceQueryServiceTests
{
    [Fact]
    public async Task ListRoutePartitionsAsyncSkipsBlankNamesAndSortsByName()
    {
        var handler = new FakeHandler(
            ListResponse(
                "listRoutePartitionResponse",
                "routePartition",
                ("""<routePartition uuid="p1"><name>Zeta</name><description>Z desc</description></routePartition>"""),
                ("""<routePartition uuid="p2"><name></name><description>blank name</description></routePartition>"""),
                ("""<routePartition uuid="p3"><name>Alpha</name><description>A desc</description></routePartition>""")));
        using var cucm = CreateService(handler);
        var service = new CucmResourceQueryService(cucm);

        var results = await service.ListRoutePartitionsAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Zeta"], results.Select(resource => resource.Name));
    }

    [Fact]
    public async Task ListDevicePoolsAsyncPropagatesCancellation()
    {
        var handler = new FakeHandler(
            ListResponse("listDevicePoolResponse", "devicePool", """<devicePool uuid="d1"><name>Pool</name></devicePool>"""));
        using var cucm = CreateService(handler);
        var service = new CucmResourceQueryService(cucm);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListDevicePoolsAsync(cts.Token));
    }

    [Fact]
    public async Task ListCallPickupGroupsAsyncReturnsNamedResources()
    {
        var handler = new FakeHandler(
            ListResponse(
                "listCallPickupGroupResponse",
                "callPickupGroup",
                """<callPickupGroup uuid="g1"><name>HS-Pickup</name><description>High school</description></callPickupGroup>"""));
        using var cucm = CreateService(handler);
        var service = new CucmResourceQueryService(cucm);

        var results = await service.ListCallPickupGroupsAsync(CancellationToken.None);

        var group = Assert.Single(results);
        Assert.Equal("HS-Pickup", group.Name);
        Assert.Equal("High school", group.Description);
    }

    private static string ListResponse(string responseElement, string itemElement, params string[] items) =>
        $"""
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Body>
            <{responseElement}>
              <return>
                {string.Join(Environment.NewLine, items)}
              </return>
            </{responseElement}>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    private static CucmService CreateService(HttpMessageHandler handler) =>
        new(
            new CucmServiceConfig("https://cucm.example.test/axl/", "15.0", "user", "password"),
            new HttpClient(handler));

    private sealed class FakeHandler(string responseBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            };
        }
    }
}
