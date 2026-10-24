using System.Net;
using PulseSharp.Stores;
using PulseSharp.Sync;

namespace PulseSharp.Tests;

public sealed partial class SyncTests
{
    [Fact]
    public async Task ALostAnswerToAPushNeverSendsAnEventTwice()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new BadNetwork(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        await EmitSales(till, 3);

        network.LoseTheAnswerTo("/push", times: 1);
        var lost = await till.Sync.SyncNow();
        var recovered = await till.Sync.SyncNow();

        Assert.False(lost.Online);
        Assert.True(recovered.Online, recovered.LastError);
        await Eventually.True(() => cloud.Sales.Count == 3, "the cloud handles the three sales");
        await Eventually.StaysTrue(() => cloud.Sales.Count == 3, "the cloud handles each sale once");
        Assert.Equal(3, (await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None)).Count);
    }

    [Fact]
    public async Task ALostAnswerInTheMiddleOfALargeBacklogLosesAndRepeatsNothing()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new BadNetwork(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network, sync => sync.BatchSize = 50);
        await till.Sync.SyncNow();
        await EmitSales(till, 120);

        network.LoseTheAnswerTo("/push", times: 1, afterRequests: 1);
        var lost = await till.Sync.SyncNow();
        var recovered = await till.Sync.SyncNow();

        Assert.False(lost.Online);
        Assert.True(recovered.Online, recovered.LastError);
        await Eventually.True(() => cloud.Sales.Count == 120, "the cloud handles all 120 sales");
        await Eventually.StaysTrue(() => cloud.Sales.Count == 120, "the cloud handles each sale once");
        Assert.Equal(120, cloud.Sales.Select(sale => sale.Number).Distinct().Count());
        Assert.Equal(120, (await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 1000), None)).Count);
    }

    [Fact]
    public async Task AServerErrorAfterTheServerSavedTheEventsNeverSendsThemTwice()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new BadNetwork(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        await EmitSales(till, 3);

        network.ReplyAfterSaving("/push", HttpStatusCode.BadGateway, times: 1);
        var failed = await till.Sync.SyncNow();
        var recovered = await till.Sync.SyncNow();

        Assert.False(failed.Online);
        Assert.Contains("502", failed.LastError, StringComparison.Ordinal);
        Assert.True(recovered.Online, recovered.LastError);
        await Eventually.True(() => cloud.Sales.Count == 3, "the cloud handles the three sales");
        await Eventually.StaysTrue(() => cloud.Sales.Count == 3, "the cloud handles each sale once");
    }

    [Fact]
    public async Task ALostAnswerToAPullNeitherLosesNorRepeatsAnEvent()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new BadNetwork(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));
        await cloud.Pulse.Emit("Product-2", new ProductChanged("B"));
        await cloud.Pulse.Emit("Product-3", new ProductChanged("C"));

        network.LoseTheAnswerTo("/pull", times: 1);
        var lost = await till.Sync.SyncNow();
        var recovered = await till.Sync.SyncNow();

        Assert.False(lost.Online);
        Assert.True(recovered.Online, recovered.LastError);
        await Eventually.True(() => till.Products.Count == 3, "the till handles the three products");
        await Eventually.StaysTrue(() => till.Products.Count == 3, "the till handles each product once");
    }

    [Fact]
    public async Task APushTheServerRefusesDoesNotStopTheDeviceFromReceiving()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new BadNetwork(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        await till.Pulse.Emit(new SaleCompleted(1));
        await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));

        network.RefuseAlways("/push", HttpStatusCode.BadRequest);
        var status = await till.Sync.SyncNow();

        Assert.False(status.Online);
        Assert.Contains("refused", status.LastError, StringComparison.Ordinal);
        await Eventually.True(() => till.Products.Count == 1, "the till still receives the product");
    }

    [Fact]
    public async Task APullTheServerRefusesDoesNotStopTheDeviceFromSending()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new BadNetwork(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        await till.Pulse.Emit(new SaleCompleted(1));

        network.RefuseAlways("/pull", HttpStatusCode.InternalServerError);
        var status = await till.Sync.SyncNow();

        Assert.False(status.Online);
        await Eventually.True(() => cloud.Sales.Count == 1, "the cloud still receives the sale");
    }

    private static async Task EmitSales(SyncDevice till, int count)
    {
        for (var number = 1; number <= count; number++)
        {
            await till.Pulse.Emit(new SaleCompleted(number));
        }
    }

    private sealed class BadNetwork(HttpMessageHandler server) : DelegatingHandler(server)
    {
        private readonly List<Fault> _faults = [];

        public void LoseTheAnswerTo(string path, int times, int afterRequests = 0)
        {
            Add(new Fault(path, FaultKind.LoseAnswer, HttpStatusCode.OK, times, afterRequests));
        }

        public void ReplyAfterSaving(string path, HttpStatusCode status, int times)
        {
            Add(new Fault(path, FaultKind.ReplyAfterServerSaved, status, times, 0));
        }

        public void RefuseAlways(string path, HttpStatusCode status)
        {
            Add(new Fault(path, FaultKind.Refuse, status, int.MaxValue, 0));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fault = Take(request.RequestUri!.AbsolutePath);
            if (fault is { Kind: FaultKind.Refuse })
            {
                return new HttpResponseMessage(fault.Status) { Content = new StringContent("Refused on purpose.") };
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (fault is null)
            {
                return response;
            }

            response.Dispose();
            if (fault.Kind == FaultKind.LoseAnswer)
            {
                throw new HttpRequestException("The connection was lost.");
            }

            return new HttpResponseMessage(fault.Status);
        }

        private void Add(Fault fault)
        {
            lock (_faults)
            {
                _faults.Add(fault);
            }
        }

        private Fault? Take(string path)
        {
            lock (_faults)
            {
                var fault = _faults.FirstOrDefault(candidate => path.EndsWith(candidate.Path, StringComparison.Ordinal) && candidate.TimesLeft > 0);
                if (fault is null)
                {
                    return null;
                }

                if (fault.SkipFirst > 0)
                {
                    fault.SkipFirst--;
                    return null;
                }

                fault.TimesLeft--;
                return fault;
            }
        }

        private enum FaultKind
        {
            LoseAnswer,
            ReplyAfterServerSaved,
            Refuse
        }

        private sealed class Fault(string path, FaultKind kind, HttpStatusCode status, int times, int skipFirst)
        {
            public string Path { get; } = path;

            public FaultKind Kind { get; } = kind;

            public HttpStatusCode Status { get; } = status;

            public int TimesLeft { get; set; } = times;

            public int SkipFirst { get; set; } = skipFirst;
        }
    }
}
