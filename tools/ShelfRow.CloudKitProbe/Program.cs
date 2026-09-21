using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ShelfRow.CloudKit;
using ShelfRow.Data;

// Must match the Sign In Callback registered on the API token in CloudKit Console.
const string CallbackPrefix = "http://localhost:49152/";

// Diagnostic CLI: signs in to CloudKit Web Services, dumps the Core Data zone,
// and reports the record types and fields the Mac app actually writes.
//
//   dotnet run --project tools/ShelfRow.CloudKitProbe -- <apiToken> [development|production]

if (args.Length >= 2 && args[0] == "--analyze")
{
    var dumped = JsonSerializer.Deserialize<List<CKRecord>>(await File.ReadAllTextAsync(args[1]))!;
    Summarize(dumped);
    Verify(dumped);
    return 0;
}

bool syncMode = args.Length > 0 && args[0] == "--sync";
if (syncMode)
    args = args[1..];

string? apiToken = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("SHELFROW_CK_API_TOKEN");
if (string.IsNullOrWhiteSpace(apiToken))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/ShelfRow.CloudKitProbe -- <apiToken> [development|production]");
    Console.Error.WriteLine("The API token comes from CloudKit Console > API Access > API Tokens.");
    return 1;
}

apiToken = apiToken.Trim();

var config = new CloudKitConfiguration
{
    ApiToken = apiToken,
    Environment = args.Length > 1 ? args[1] : "production"
};

string tokenCachePath = Path.Combine(AppContext.BaseDirectory, "webauth.token");
if (File.Exists(tokenCachePath))
{
    config.WebAuthToken = File.ReadAllText(tokenCachePath).Trim();
    Console.WriteLine($"Reusing cached web auth token from {tokenCachePath}");
}

var client = new CloudKitClient(config);
client.WebAuthTokenRenewed += token => File.WriteAllText(tokenCachePath, token);

Console.WriteLine($"Container  : {config.ContainerIdentifier}");
Console.WriteLine($"Environment: {config.Environment}");
Console.WriteLine($"API token  : {Mask(apiToken)} ({apiToken.Length} chars)");

if (apiToken.Length != 64 || !apiToken.All(char.IsAsciiHexDigit))
{
    Console.WriteLine();
    Console.WriteLine("  WARNING: a CloudKit API token is 64 hexadecimal characters.");
    Console.WriteLine("  What was passed is not that shape, so it is probably the wrong value");
    Console.WriteLine("  (a Server-to-Server key ID, a truncated paste, or a token from another container).");
}

Console.WriteLine();

if (syncMode)
    return await RunSyncAsync(client, config, tokenCachePath);

var allRecords = new List<CKRecord>();
string? syncToken = null;

while (true)
{
    CKChangesZoneResponse response;
    try
    {
        response = await client.FetchZoneChangesAsync(syncToken, resultsLimit: 200);
    }
    catch (CloudKitException ex) when (ex.IsAuthenticationRequired && ex.RedirectUrl != null)
    {
        if (!await SignInAsync(ex.RedirectUrl, config, tokenCachePath))
            return 1;
        continue;
    }
    catch (CloudKitException ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        if (ex.ServerErrorCode == "ZONE_NOT_FOUND")
            Console.Error.WriteLine("The Core Data zone does not exist in this environment. Try the other environment.");
        return 1;
    }

    var zone = response.Zones?.FirstOrDefault();
    if (zone == null)
    {
        Console.Error.WriteLine("Response contained no zones.");
        return 1;
    }

    if (zone.Records != null)
        allRecords.AddRange(zone.Records);

    Console.WriteLine($"Fetched {allRecords.Count} records...");

    syncToken = zone.SyncToken;
    if (!zone.MoreComing)
        break;
}

Summarize(allRecords);

string dumpPath = Path.Combine(AppContext.BaseDirectory, "zone-dump.json");
await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(allRecords, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Full dump written to {dumpPath}");
return 0;

// Runs the production sync path end to end into a throwaway database, so the whole
// pipeline is exercised against the live library rather than against fixtures.
static async Task<int> RunSyncAsync(CloudKitClient client, CloudKitConfiguration config, string tokenCachePath)
{
    string dbPath = Path.Combine(AppContext.BaseDirectory, "sync-test.db");
    foreach (string stale in Directory.GetFiles(AppContext.BaseDirectory, "sync-test.db*"))
        File.Delete(stale);

    using var repository = new SqliteShelfRowRepository(dbPath);
    await repository.InitializeAsync();

    var engine = new CloudKitSyncEngine(client, repository);
    var progress = new Progress<int>(n => Console.Write($"\r  processed {n} records..."));
    var started = Stopwatch.StartNew();

    CloudKitSyncEngine.SyncResult result;
    while (true)
    {
        try
        {
            result = await engine.SyncDownAsync(progress);
            break;
        }
        catch (CloudKitException ex) when (ex.IsAuthenticationRequired && ex.RedirectUrl != null)
        {
            if (!await SignInAsync(ex.RedirectUrl, config, tokenCachePath))
                return 1;
        }
        catch (CloudKitException ex)
        {
            Console.Error.WriteLine($"\nFAILED: {ex.Message}");
            return 1;
        }
    }

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"=== sync finished in {started.Elapsed.TotalSeconds:F1}s ===");
    Console.WriteLine($"  items    : {result.Items}");
    Console.WriteLine($"  shelves  : {result.Shelves}");
    Console.WriteLine($"  volumes  : {result.Volumes}");
    Console.WriteLine($"  links    : {result.Links}");
    Console.WriteLine($"  deletions: {result.Deletions}");
    Console.WriteLine();

    Console.WriteLine($"stored item count : {await repository.GetItemCountAsync()}");

    var volumes = await repository.GetVolumesAsync();
    foreach (var volume in volumes)
        Console.WriteLine($"  volume: {volume.Name}  ({volume.LastKnownPath})");

    var shelves = await repository.GetShelvesAsync();
    Console.WriteLine($"stored shelf count: {shelves.Count}");
    foreach (var shelf in shelves.Take(10))
    {
        int members = await repository.GetItemCountAsync(shelf.Id);
        Console.WriteLine($"  shelf: {shelf.Title,-28} {members} books");
    }

    Console.WriteLine();
    Console.WriteLine($"Database written to {dbPath}");
    return 0;
}

static void Summarize(List<CKRecord> records)
{
    Console.WriteLine();
    Console.WriteLine($"=== {records.Count} records in {CloudKitMapper.CoreDataZoneName} ===");
    Console.WriteLine();

    foreach (var group in records.GroupBy(r => r.RecordType).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"{group.Key}  ({group.Count()} records)");

        var types = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var present = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in group)
        {
            foreach (var (name, field) in record.Fields)
            {
                types[name] = field.Type ?? DescribeValue(field.Value);
                present[name] = present.GetValueOrDefault(name) + 1;
            }
        }

        foreach (var (name, type) in types)
            Console.WriteLine($"    {name,-36} {type,-12} in {present[name]}/{group.Count()}");

        var sample = group.First();
        Console.WriteLine($"    -- sample recordName: {sample.RecordName}");
        foreach (var (name, field) in sample.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            string text = JsonSerializer.Serialize(field.Value);
            if (text.Length > 100) text = text[..100] + "...";
            Console.WriteLine($"       {name,-34} {text}");
        }

        Console.WriteLine();
    }
}

// Runs the real mapper over the dump, so mapping defects surface against the whole
// library rather than against a handful of hand-written fixtures.
static void Verify(List<CKRecord> records)
{
    Console.WriteLine("=== mapper verification ===");

    var volumesByRecordName = new Dictionary<string, Guid>(StringComparer.Ordinal);
    var itemsByRecordName = new Dictionary<string, Guid>(StringComparer.Ordinal);
    var shelvesByRecordName = new Dictionary<string, Guid>(StringComparer.Ordinal);

    int items = 0, shelves = 0, volumes = 0, links = 0;
    int itemsWithoutId = 0, itemsWithoutVolume = 0, unreadableLinks = 0;

    foreach (var record in records)
    {
        switch (record.RecordType)
        {
            case CloudKitMapper.VolumeRecordType:
                var volume = CloudKitMapper.ToVolume(record)!;
                volumesByRecordName[record.RecordName] = volume.Id;
                volumes++;
                break;

            case CloudKitMapper.ShelfRecordType:
                var shelf = CloudKitMapper.ToShelf(record)!;
                shelvesByRecordName[record.RecordName] = shelf.Id;
                shelves++;
                break;

            case CloudKitMapper.ItemRecordType:
                var item = CloudKitMapper.ToItem(record)!;
                itemsByRecordName[record.RecordName] = item.Id;
                items++;
                if (item.Id == Guid.Empty) itemsWithoutId++;
                if (string.IsNullOrEmpty(item.VolumeRecordName)) itemsWithoutVolume++;
                break;

            case CloudKitMapper.ManyToManyRecordType:
                if (CloudKitMapper.ToManyToManyLink(record) == null)
                    unreadableLinks++;
                else
                    links++;
                break;
        }
    }

    Console.WriteLine($"  volumes mapped         : {volumes}");
    Console.WriteLine($"  shelves mapped         : {shelves}");
    Console.WriteLine($"  items mapped           : {items}");
    Console.WriteLine($"  item-shelf links read  : {links} (unreadable: {unreadableLinks})");
    Console.WriteLine($"  items with no CD_id    : {itemsWithoutId}");
    Console.WriteLine($"  items with no volume   : {itemsWithoutVolume}");

    int danglingVolume = 0, danglingLinkItem = 0, danglingLinkShelf = 0;
    foreach (var record in records)
    {
        if (record.RecordType == CloudKitMapper.ItemRecordType)
        {
            var item = CloudKitMapper.ToItem(record)!;
            if (item.VolumeRecordName is { Length: > 0 } vrn && !volumesByRecordName.ContainsKey(vrn))
                danglingVolume++;
        }
        else if (record.RecordType == CloudKitMapper.ManyToManyRecordType)
        {
            if (CloudKitMapper.ToManyToManyLink(record) is { } link)
            {
                if (!itemsByRecordName.ContainsKey(link.LeftRecordName)) danglingLinkItem++;
                if (!shelvesByRecordName.ContainsKey(link.RightRecordName)) danglingLinkShelf++;
            }
        }
    }

    Console.WriteLine($"  unresolvable volume refs: {danglingVolume}");
    Console.WriteLine($"  links with unknown item : {danglingLinkItem}");
    Console.WriteLine($"  links with unknown shelf: {danglingLinkShelf}");

    var duplicateIds = itemsByRecordName.Values.GroupBy(g => g).Count(g => g.Count() > 1);
    Console.WriteLine($"  duplicate item CD_id    : {duplicateIds}");
}

static string Mask(string token) =>
    token.Length <= 12 ? new string('*', token.Length) : $"{token[..6]}...{token[^6..]}";

static async Task<bool> SignInAsync(string redirectUrl, CloudKitConfiguration config, string tokenCachePath)
{
    Console.WriteLine("Apple ID sign-in required. Opening your browser...");
    Console.WriteLine();
    Console.WriteLine("  TICK \"Keep me signed in\", or the token expires in 30 minutes.");
    Console.WriteLine();

    using var listener = StartCallbackListener();
    if (listener == null)
    {
        Console.WriteLine("  (Could not listen on " + CallbackPrefix + " - falling back to manual paste.)");
    }

    try
    {
        Process.Start(new ProcessStartInfo(redirectUrl) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (Could not open the browser automatically: {ex.Message})");
        Console.WriteLine("  Open this URL yourself:");
        Console.WriteLine();
        Console.WriteLine("  " + redirectUrl);
        Console.WriteLine();
    }

    Console.WriteLine("Waiting for the callback. If the browser lands on a page that fails to load,");
    Console.WriteLine("paste that URL here instead and press Enter:");
    Console.Write("> ");

    var pastedTask = Task.Run(Console.ReadLine);
    var tasks = new List<Task<string?>> { pastedTask };
    if (listener != null)
        tasks.Add(AwaitCallbackAsync(listener));

    string? captured = await await Task.WhenAny(tasks);

    if (string.IsNullOrWhiteSpace(captured))
    {
        Console.Error.WriteLine("No token received.");
        return false;
    }

    string token = ExtractToken(captured.Trim());
    config.WebAuthToken = token;
    File.WriteAllText(tokenCachePath, token);
    Console.WriteLine();
    Console.WriteLine("Token captured. Retrying...");
    Console.WriteLine();
    return true;
}

static HttpListener? StartCallbackListener()
{
    try
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(CallbackPrefix);
        listener.Start();
        return listener;
    }
    catch (HttpListenerException)
    {
        return null;
    }
}

static async Task<string?> AwaitCallbackAsync(HttpListener listener)
{
    while (true)
    {
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception)
        {
            return null;
        }

        string? token = context.Request.QueryString["ckWebAuthToken"];

        byte[] body = Encoding.UTF8.GetBytes(token == null
            ? "<html><body><h2>No token in this request.</h2></body></html>"
            : "<html><body><h2>Signed in. You can close this tab.</h2></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();

        if (token != null)
            return token;
    }
}

static string ExtractToken(string pasted)
{
    if (!Uri.TryCreate(pasted, UriKind.Absolute, out var uri))
        return pasted;

    foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = pair.Split('=', 2);
        if (parts.Length == 2 && parts[0].Equals("ckWebAuthToken", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(parts[1]);
    }

    return pasted;
}

static string DescribeValue(object? value) => value switch
{
    null => "null",
    JsonElement { ValueKind: JsonValueKind.Object } obj when obj.TryGetProperty("recordName", out _) => "REFERENCE",
    JsonElement element => element.ValueKind.ToString().ToUpperInvariant(),
    _ => value.GetType().Name
};
