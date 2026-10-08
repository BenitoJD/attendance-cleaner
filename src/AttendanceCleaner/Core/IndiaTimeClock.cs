using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AttendanceCleaner.Core;

/// <summary>Provides a continuously advancing India Standard Time clock.</summary>
public sealed class IndiaTimeClock
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);
    private static readonly Uri TimeApiEndpoint = new(
        "https://timeapi.io/api/Time/current/zone?timeZone=Asia%2FKolkata");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _sync = new();
    private readonly HttpClient _httpClient;
    private DateTimeOffset _anchorTime = DateTimeOffset.UtcNow.ToOffset(IndiaOffset);
    private long _anchorTimestamp = Stopwatch.GetTimestamp();

    public IndiaTimeClock() : this(SharedHttpClient) { }

    public IndiaTimeClock(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public DateTimeOffset CurrentTime
    {
        get
        {
            lock (_sync)
                return _anchorTime + Stopwatch.GetElapsedTime(_anchorTimestamp);
        }
    }

    /// <summary>
    /// Synchronizes against the free TimeAPI endpoint. On failure, the clock keeps
    /// advancing from the device's UTC clock and the caller can show an offline state.
    /// </summary>
    public async Task<bool> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var requestStarted = Stopwatch.GetTimestamp();
        try
        {
            using var response = await _httpClient.GetAsync(
                TimeApiEndpoint,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<TimeApiResponse>(
                stream, JsonOptions, cancellationToken);

            if (payload is null || !string.Equals(payload.TimeZone, "Asia/Kolkata", StringComparison.Ordinal))
                return false;

            var serverTime = new DateTimeOffset(
                payload.Year,
                payload.Month,
                payload.Day,
                payload.Hour,
                payload.Minute,
                payload.Seconds,
                payload.MilliSeconds,
                IndiaOffset);
            var requestFinished = Stopwatch.GetTimestamp();
            var midpoint = requestStarted + (requestFinished - requestStarted) / 2;

            lock (_sync)
            {
                _anchorTime = serverTime;
                _anchorTimestamp = midpoint;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or IOException
                or JsonException
                or OperationCanceledException
                or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed class TimeApiResponse
    {
        [JsonPropertyName("year")] public int Year { get; init; }
        [JsonPropertyName("month")] public int Month { get; init; }
        [JsonPropertyName("day")] public int Day { get; init; }
        [JsonPropertyName("hour")] public int Hour { get; init; }
        [JsonPropertyName("minute")] public int Minute { get; init; }
        [JsonPropertyName("seconds")] public int Seconds { get; init; }
        [JsonPropertyName("milliSeconds")] public int MilliSeconds { get; init; }
        [JsonPropertyName("timeZone")] public string? TimeZone { get; init; }
    }
}
