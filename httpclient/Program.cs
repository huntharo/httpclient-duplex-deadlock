#define USE_SOCKETS_HTTP_HANDLER
#define USE_INSECURE_CIPHER_FOR_WIRESHARK

using System.Text;
using System.Diagnostics;

var httpClient = SetupHttpClient.Client;

int totalRequestCount = 0;

ThreadPool.SetMinThreads(100, 100);

var TEST_MODE = Environment.GetEnvironmentVariable("TEST_MODE") ?? "complete-deadlock-many-requests";

// Validate and print info about which mode was selected
if (TEST_MODE == "complete-deadlock-one-request")
{
  Console.WriteLine("TEST_MODE: complete-deadlock-one-request");
}
else if (TEST_MODE == "complete-deadlock-one-request-with-headers")
{
  Console.WriteLine("TEST_MODE: complete-deadlock-one-request-with-headers");
}
else if (TEST_MODE == "complete-deadlock-many-requests")
{
  Console.WriteLine("TEST_MODE: complete-deadlock-many-requests");
}
else if (TEST_MODE == "partial-deadlock-many-requests")
{
  Console.WriteLine("TEST_MODE: partial-deadlock-many-requests");
}
else if (TEST_MODE == "partial-deadlock-with-half-duplex-requests")
{
  Console.WriteLine("TEST_MODE: partial-deadlock-with-half-duplex-requests");
}
else
{
  Console.WriteLine("TEST_MODE: unknown");
  // Print the allowed values
  Console.WriteLine("TEST_MODE options: complete-deadlock-one-request, complete-deadlock-one-request-with-headers, complete-deadlock-many-requests, partial-deadlock-many-requests, partial-deadlock-with-half-duplex-requests");
  return;
}

var duplexTaskCount = TEST_MODE == "complete-deadlock-one-request" || TEST_MODE == "complete-deadlock-one-request-with-headers" ? 1 : 10;
var sendHalfDuplexRequestsToo = TEST_MODE == "partial-deadlock-with-half-duplex-requests";
var sendLotsOfHeaders = TEST_MODE == "partial-deadlock-many-requests" || TEST_MODE == "complete-deadlock-one-request-with-headers";

var sw = Stopwatch.StartNew();

// Setup the duplex tasks
var duplexTasks = new List<Task>();
for (int j = 0; j < duplexTaskCount; j++)
{
  var offset = j;

  duplexTasks.Add(Task.Run(async () =>
  {
    for (int i = 0; i < 10; i++)
    {
      await HandleSingleRequest(offset + i);

      await Task.Delay(TimeSpan.FromSeconds(1));
    }

    Console.WriteLine($"{offset}: Finished with requests");
  }));
}

// Setup a cancellation token
var cts = new CancellationTokenSource();

// Print number of initiated requests every 5 seconds
var whereAmITask = Task.Run(async () =>
{
  while (!cts.IsCancellationRequested)
  {
    try {
      await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
    } catch (TaskCanceledException) {
      // Ignore
    }
    Console.WriteLine($"Requests Initiated: {totalRequestCount}");
  }
});

// Setup the half duplex task
var halfDuplexTask = sendHalfDuplexRequestsToo ? Task.Run(async () =>
{
  while(!cts.IsCancellationRequested)
  {
    await HandleHalfDuplexRequest();

    try {
      await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
    } catch (TaskCanceledException) {
      // Ignore
    }
  }

  Console.WriteLine("Half Duplex: Finished with requests");
}) : Task.CompletedTask;

await Task.WhenAll(duplexTasks);
cts.Cancel();
await Task.WhenAll(halfDuplexTask, whereAmITask);

Console.WriteLine($"Total Requests: {totalRequestCount}");
Console.WriteLine($"Total Time: {sw.Elapsed.TotalSeconds} seconds");

// Indicate what happened
if (TEST_MODE == "complete-deadlock-one-request")
{
  Console.WriteLine("TEST_MODE: complete-deadlock-one-request");
  Console.WriteLine("This started 1 duplex request, but did not send tons of headers to force a flush.");
  Console.WriteLine("This will take ~1 seconds with the patched HttpClient, but will deadlock with the unpatched HttpClient.");
}
else if (TEST_MODE == "complete-deadlock-many-requests")
{
  Console.WriteLine("TEST_MODE: complete-deadlock-many-requests");
  Console.WriteLine("This started 10 duplex requests, but did not send tons of headers to force a flush.");
  Console.WriteLine("This will take ~10 seconds with the patched HttpClient, but will deadlock with the unpatched HttpClient.");
}
else if (TEST_MODE == "partial-deadlock-many-requests")
{
  Console.WriteLine("TEST_MODE: partial-deadlock-many-requests");
  Console.WriteLine("This started 10 duplex requests AND sent tons of headers to force a flush.");
  Console.WriteLine("This will take ~10 seconds with either HttpClient, but there is a partial deadlock until later requests send more headers, forcing a flush.");
}
else if (TEST_MODE == "partial-deadlock-with-half-duplex-requests")
{
  Console.WriteLine("TEST_MODE: partial-deadlock-with-half-duplex-requests");
  Console.WriteLine("This started 10 duplex requests and sends a half duplex request every 5 seconds, which forces a flush.");
  Console.WriteLine("This will take ~10 seconds with the patched HttpClient, but will take ~50 seconds with the unpatched HttpClient because the half duplex client is the only thing that forces a flush every 5 seconds.");
}

async Task HandleSingleRequest(int myRequestId)
{
    Interlocked.Increment(ref totalRequestCount);

    var duplexContent = new HttpDuplexContent();
    duplexContent.Headers.Add("Content-Type", "text/plain; charset=utf-8");
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost:5050/any")
    {
      Version = new Version(2, 0),
      Content = duplexContent
    };
    request.Headers.Host = "localhost:5050";
    // Add enough request headers to force a flush
    if (sendLotsOfHeaders)
    {
      for (int j = 0; j < 70; j++)
      {
        request.Headers.Add($"X-Header-{j}", $"Value-Really-Long-Hi-Mom-What-Is-For-Dinner-Question-Mark-Was-Your-Day-Nice-Question-Mark-{j}");
      }
    }
    // request.Headers.Add("Date", DateTime.UtcNow.ToString("R"));

    Console.WriteLine($"{myRequestId}: Sending request");

    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    var requestTask = WriteRequestAsync(duplexContent, myRequestId);
    var responseTask = ReadResponseAsync(response, myRequestId);

    Console.WriteLine($"{myRequestId}: Received response headers");

    await Task.WhenAll(
      responseTask,
      requestTask
    );

    Console.WriteLine($"{myRequestId}: Finished reading response and writing request");
}

async Task ReadResponseAsync(HttpResponseMessage response, int i)
{
    if(!response.IsSuccessStatusCode)
    {
      Console.WriteLine($"{i}: Error: {response.StatusCode}");
      throw new Exception($"{i}: Error: {response.StatusCode}");
    }

    Console.WriteLine($"{i}: Got response status code: {response.StatusCode}");

    using (var responseContentReader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true))
    {
        var responseContent = await responseContentReader.ReadToEndAsync();
        // Console.WriteLine($"{i}: Response: {responseContent}");
    }

    Console.WriteLine($"{i}: Finished reading response");
}

async Task WriteRequestAsync(HttpDuplexContent duplexContent, int i)
{
    using Stream requestStream = await duplexContent.WaitForStreamAsync();

    Console.WriteLine($"{i}: Got request stream");

    using (var requestContentWriter = new StreamWriter(requestStream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
    {
      await requestContentWriter.WriteAsync($"{i}: Some Request Data\r\n");
      await requestContentWriter.FlushAsync();
      requestContentWriter.Close();
    }

    Console.WriteLine($"{i}: Finished writing request data");

    duplexContent.Complete();

    Console.WriteLine($"{i}: Marked request content complete");
}

async Task HandleHalfDuplexRequest() {
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost:5050/protocol/error")
    {
      Version = new Version(2, 0),
    };
    request.Headers.Host = "localhost:5050";
    request.Headers.Add("Date", DateTime.UtcNow.ToString("R"));
    request.Content = new StringContent("Simple Post");

    Console.WriteLine($"Half Duplex: Sending request");

    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    Console.WriteLine($"Half Duplex: Received response headers");

    using var responseContentReader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true);
    var responseContent = await responseContentReader.ReadToEndAsync();

    Console.WriteLine($"Half Duplex: Finished reading response and writing request");
}