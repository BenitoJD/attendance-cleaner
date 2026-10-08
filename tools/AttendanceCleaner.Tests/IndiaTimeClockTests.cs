using System.Net;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class IndiaTimeClockTests
{
    [Fact]
    public async Task Synchronizes_to_India_time_and_keeps_advancing()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"year":2026,"month":10,"day":8,"hour":16,"minute":37,"seconds":40,"milliSeconds":816,"timeZone":"Asia/Kolkata"}
                """),
        }));
        var clock = new IndiaTimeClock(client);

        Assert.True(await clock.SynchronizeAsync());

        var current = clock.CurrentTime;
        Assert.Equal(TimeSpan.FromMinutes(330), current.Offset);
        Assert.Equal(new DateOnly(2026, 10, 8), DateOnly.FromDateTime(current.DateTime));
        Assert.Equal(16, current.Hour);
        Assert.Equal(37, current.Minute);
        Assert.True(current.Second >= 40);
    }

    [Fact]
    public async Task Uses_device_clock_when_time_api_is_unavailable()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var clock = new IndiaTimeClock(client);
        var before = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(330));

        Assert.False(await clock.SynchronizeAsync());

        var after = clock.CurrentTime;
        Assert.Equal(TimeSpan.FromMinutes(330), after.Offset);
        Assert.InRange(after, before.AddSeconds(-1), DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(330)).AddSeconds(1));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
