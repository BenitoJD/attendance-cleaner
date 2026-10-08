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

    [Fact]
    public async Task Stalled_response_body_times_out_and_does_not_block_a_later_synchronization()
    {
        var requests = 0;
        using var stalledBody = new StalledStream();
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = ++requests == 1
                ? new StreamContent(stalledBody)
                : new StringContent("""
                    {"year":2026,"month":10,"day":8,"hour":16,"minute":37,"seconds":40,"milliSeconds":816,"timeZone":"Asia/Kolkata"}
                    """),
        }))
        { Timeout = TimeSpan.FromMilliseconds(250) };
        var clock = new IndiaTimeClock(client);

        var synchronization = clock.SynchronizeAsync();
        await stalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await synchronization.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await clock.SynchronizeAsync());
        Assert.Equal(new DateOnly(2026, 10, 8), DateOnly.FromDateTime(clock.CurrentTime.DateTime));
    }

    [Fact]
    public async Task Caller_can_cancel_while_reading_a_stalled_body()
    {
        using var stalledBody = new StalledStream();
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalledBody),
        }));
        var clock = new IndiaTimeClock(client);
        using var cancellation = new CancellationTokenSource();
        var synchronization = clock.SynchronizeAsync(cancellation.Token);
        await stalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        Assert.False(await synchronization.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
