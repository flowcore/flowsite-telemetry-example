using System.Text.Json;
using System.Text.Json.Serialization;

public static class TelemetryExample
{
    // Fixed Flowsite API host. Requests below use absolute URLs.
    public const string Host = "https://flowsite.flowcoresystems.com";

    // Maximum query window: 24 hours = 86,400,000 milliseconds.
    // The last window is shorter when less than a day remains before the current time.
    public const long DayMs = 24 * 60 * 60 * 1000L;

    // Maximum raw readings PER KEY, PER REQUEST (API default is 100).
    // PoC assumption: no telemetry key should have more than 1000 values per day
    // (in any 24-hour query window). This example does not paginate.
    public const int Limit = 1000;

    // Fetch ONE telemetry window and a current attribute snapshot.
    // client needs X-Authorization configured (e.g. Bearer YOUR_JWT).
    // cursor is the next UTC Unix millisecond to fetch; start at 0 for backfill.
    // Key lists are supplied by the caller; an empty list skips its respective query.
    public static DataResult GetData(
        HttpClient client,
        string deviceId,
        long cursor,
        IEnumerable<string> telemetryKeys,
        IEnumerable<string> attributeKeys)
    {
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));

        var keys = telemetryKeys.Distinct().ToArray();
        var attributesToFetch = attributeKeys.Distinct().ToArray();
        var endpoint = $"{Host}/api/plugins/telemetry/DEVICE/{deviceId}";
        var target = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Attributes are current values with last-update timestamps, not history.
        // The telemetry window, Limit, and cursor do not apply to attributes.
        IReadOnlyList<AttributeValue> attributes = Array.Empty<AttributeValue>();
        if (attributesToFetch.Length > 0)
        {
            var attributeList = Uri.EscapeDataString(string.Join(",", attributesToFetch));
            attributes = GetJson<List<AttributeValue>>(client,
                $"{endpoint}/values/attributes?keys={attributeList}")
                ?? throw new InvalidDataException("Invalid attributes response.");
        }

        var telemetry = new Dictionary<string, List<Point>>();
        var nextCursor = cursor;

        if (keys.Length > 0 && cursor < target)
        {
            // [cursor, end): inclusive start, exclusive end; at most one day.
            var end = Math.Min(cursor + DayMs, target);
            var keyList = Uri.EscapeDataString(string.Join(",", keys));
            var url = $"{endpoint}/values/timeseries"
                + $"?keys={keyList}&startTs={cursor}&endTs={end}"
                // NONE preserves raw values; ASC returns oldest readings first.
                + $"&agg=NONE&orderBy=ASC&limit={Limit}";

            var response = GetJson<Dictionary<string, List<Point>>>(client, url)
                ?? throw new InvalidDataException("Invalid telemetry values response.");

            foreach (var key in keys)
                telemetry[key] = response.GetValueOrDefault(key) ?? new List<Point>();

            // Empty successful windows also advance to the next window.
            nextCursor = end;
        }

        // Caught up means the telemetry cursor reached the time captured at call start.
        // An empty telemetry key list cannot advance and therefore has no more windows.
        var hasNext = keys.Length > 0 && nextCursor < target;
        return new DataResult(attributes, telemetry, nextCursor, hasNext);
    }

    // Make one request. Errors propagate to the caller.
    private static T? GetJson<T>(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = client.Send(request);
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        return JsonSerializer.Deserialize<T>(stream);
    }

    public sealed record DataResult(
        IReadOnlyList<AttributeValue> Attributes,
        Dictionary<string, List<Point>> Telemetry,
        long NextCursor,
        bool HasNext);

    public sealed record Point(
        // Measurement timestamp in UTC Unix milliseconds; preserve the JSON value type.
        [property: JsonPropertyName("ts")] long Ts,
        [property: JsonPropertyName("value")] JsonElement Value);

    public sealed record AttributeValue(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] JsonElement Value,
        // Last modification time in UTC Unix milliseconds, not a history cursor.
        [property: JsonPropertyName("lastUpdateTs")] long LastUpdateTs);
}
