# Calling Flowsite GetData

`GetData` returns `Attributes`, `Telemetry`, `NextCursor`, and `HasNext`.
Each call fetches up to one day of telemetry and a current attribute snapshot.

## Login and JWT

Call `POST https://flowsite.flowcoresystems.com/api/auth/login` with a JSON body
containing `username` and `password`. Use the returned `token` as the JWT in
`X-Authorization: Bearer YOUR_JWT`. [Flowsite login documentation](https://www.postman.com/flowcoresystems/workspace/flowcore-systems-public/request/31503085-7e93606d-b08b-4bed-a36d-2a7099406763?action=share&creator=31503085)

Keep and reuse the JWT. Before each `GetData` call, check its `exp` timestamp
(UTC Unix seconds). If it is missing, expired, or about to expire, call login again
and replace the authorization header with the new JWT. Allow a small margin,
such as 30 seconds. **Do not log in before every request.** The caller handles this;
login and token renewal are omitted from the PoC code.

## Get the device list

Use [Flowsite List Devices](https://www.postman.com/flowcoresystems/flowcore-systems-public/http-request/bch74ze/list-devices)
to get device IDs dynamically. For example, fetch the first page of Raptors:

```http
GET https://flowsite.flowcoresystems.com/api/user/devices?pageSize=100&page=0&type=575
X-Authorization: Bearer YOUR_JWT
```

- `pageSize`: maximum devices returned per page; this example requests 100.
- `page`: starts at `0`. Increase it by one while the response's `hasNext` is true.
- `type`: optional model filter. **425 = Phantom**, **575 = Raptor**. Omit it to
  list all devices available to the logged-in user. To get both models only, make
  a separate paged query for each type.
- Read devices from the response's `data` array. Each device's UUID is in `id.id`.
  Pass those UUIDs to `GetData` in place of the hard coded IDs below.

The device list's `hasNext` controls device pages; `GetData`'s `HasNext` controls
telemetry time windows. Refresh the device list periodically to find new devices.
Start new devices at cursor `0` and reuse the saved cursor for existing devices.

## Basic example

Run this inside your application. Replace the device IDs, token,
and attribute names. Implement the three storage methods using your chosen storage:
`LoadCursor` returns a saved `long?` (null for a new device), `SaveCursor`
saves progress, and `SaveData` saves attributes and telemetry before returning.
These are placeholders for your storage code. All calls are synchronous and block
the calling thread while waiting for HTTP requests.

```csharp
using var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-Authorization", "Bearer YOUR_JWT");

var deviceIds = new[] { "DEVICE_UUID_1", "DEVICE_UUID_2" };
var telemetryKeys = new[] { "chemical_name", "system_status", "ppm" };
var attributeKeys = new[] { "serial", "model" };

foreach (var deviceId in deviceIds)
{
    // One saved cursor per device. A new device starts at timestamp 0.
    long cursor = LoadCursor(deviceId) ?? 0;

    bool hasNext;
    do
    {
        // Ensure the saved JWT is still fresh; see the login guidance above.
        var result = TelemetryExample.GetData(
            client, deviceId, cursor, telemetryKeys, attributeKeys);

        // Save the data BEFORE advancing this device's cursor.
        SaveData(deviceId, result.Attributes, result.Telemetry);

        // Save progress after every successful window, including empty windows.
        SaveCursor(deviceId, result.NextCursor);

        cursor = result.NextCursor;
        hasNext = result.HasNext;
    } while (hasNext);
}
```

## Catch-up and restart

- `HasNext = true`: call again immediately with `NextCursor` to fetch the next window.
- `HasNext = false`: caught up to the time captured at the start of that call. It does
  not mean no future data will arrive. Run the device loop again later, for example
  after ten minutes. An empty telemetry key list also returns false.
- Empty telemetry does not mean backfill is finished. Continue using `HasNext`.
- Keep one cursor per device in persistent storage of your choice. After a restart,
  load each device's cursor and resume from its saved timestamp.
- Keep one active loop per device. If saving data fails, leave its cursor unchanged.
  A crash between saving data and saving the cursor can replay a window. Upsert
  telemetry by `(deviceId, key, timestamp)` to avoid duplicates.

## Retries and errors

The PoC makes each API request once. Errors reach the caller; without error handling,
an exception stops the example loop. Add retry handling in your application:

- Retry **429**, **408**, **5xx**, network failures, and HTTP timeouts.
- Use increasing delays, for example **1, 2, 4, and 8 seconds**, with a small random delay.
- When `Retry-After` is present, wait at least that long. It can specify seconds or
  an HTTP date. [Retry-After reference](https://learn.microsoft.com/en-us/microsoft-cloud/dev/dev-proxy/concepts/retry-after-header)
- Retry `GetData` with the same device, keys, and cursor. Do not advance the cursor
  after a failed call. Set a retry limit, then stop or defer the device if failures
  continue. A later run resumes from the saved cursor.
- For **401**, obtain a fresh JWT and retry with the same cursor. If it still fails,
  check credentials. For **403**, check permissions. Do not blindly retry other
  HTTP errors or invalid JSON.
