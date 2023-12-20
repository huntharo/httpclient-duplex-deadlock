using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Timeouts;
using System.Text;

[Route("/any")]
public class IncomingController : ControllerBase
{
  private static volatile int requestCount = 0;

  private static bool causeClientProtocolErrorRaceCondition = Environment.GetEnvironmentVariable("PROTOCOL_ERROR_TRIGGER") == "true";

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  public async Task HandleRequest()
  {
    Interlocked.Increment(ref requestCount);

    // Console.WriteLine($"{requestCount}: Received request");

    Response.ContentType = "text/plain";
    Response.StatusCode = 200;
    Response.Headers.Add("Date", DateTime.UtcNow.ToString("R"));
    // Add some dummy headers
    for (int j = 0; j < 5; j++)
    {
      Response.Headers.Add($"X-Header-{j}", $"Value-{j}");
    }

    // Triggers a race condition where the `DATA` response frame
    // can be sent before the `HEADERS` response frame causing
    // no error at all on Kestrel but a ProtocolError on the client.
    if (causeClientProtocolErrorRaceCondition)
    {
      Task.Run(async () =>
      {
        await Response.StartAsync();
      });
    }

    // Console.WriteLine($"{requestCount}: Finished writing response headers, started writing response data");

    for (int j = 0; j < 1; j++)
    {
      await Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{requestCount}: Some Response Data\r\n"));
      // await Response.WriteAsync($"{requestCount}: Some Response Data\r\n");
    }

    // Console.WriteLine($"{requestCount}: Finished writing response data");
    await Response.BodyWriter.FlushAsync();
    await Response.BodyWriter.CompleteAsync();

    // await Response.StartAsync();

    await Response.CompleteAsync();

    // Console.WriteLine($"{requestCount}: Completed response");

    using var requestStream = new StreamReader(this.Request.BodyReader.AsStream(), leaveOpen: true);

    // Console.WriteLine($"{requestCount}: Reading request data");

    string? line;
    while ((line = await requestStream.ReadLineAsync()) != null)
    {
        // Console.WriteLine($"{requestCount}: {line}");
    }

    // Console.WriteLine($"{requestCount}: Finished reading request data");

    await Request.BodyReader.CompleteAsync();

    // Console.WriteLine($"{requestCount}: Completed request");

    requestStream.Close();

    // Console.WriteLine($"{requestCount}: Finished with request");
  }
}