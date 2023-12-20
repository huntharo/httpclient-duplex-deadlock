using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Timeouts;

[Route("/any")]
public class IncomingController : ControllerBase
{
  private static volatile int requestCount = 0;

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  public async Task HandleRequest()
  {
    Interlocked.Increment(ref requestCount);

    Console.WriteLine($"{requestCount}: Received request");

    Response.ContentType = "text/plain";
    Response.StatusCode = 200;
    Response.Headers.Add("Date", DateTime.UtcNow.ToString("R"));
    // Add some dummy headers
    for (int j = 0; j < 25; j++)
    {
      Response.Headers.Add($"X-Header-{j}", $"Value-{j}");
    }

    await Response.StartAsync();

    Console.WriteLine($"{requestCount}: Finished writing response headers, started writing response data");

    for (int j = 0; j < 50; j++)
    {
      // await Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{i}: Some Response Data\r\n"));
      await Response.WriteAsync($"{requestCount}: Some Response Data\r\n");
    }
    Console.WriteLine($"{requestCount}: Finished writing response data");
    // await Response.BodyWriter.FlushAsync();
    // await Response.BodyWriter.CompleteAsync();
    await Response.CompleteAsync();
    Console.WriteLine($"{requestCount}: Completed response");

    using var requestStream = new StreamReader(this.Request.BodyReader.AsStream(), leaveOpen: true);

    Console.WriteLine($"{requestCount}: Reading request data");

    string? line;
    while ((line = await requestStream.ReadLineAsync()) != null)
    {
        Console.WriteLine($"{requestCount}: {line}");
    }

    Console.WriteLine($"{requestCount}: Finished reading request data");

    await Request.BodyReader.CompleteAsync();

    Console.WriteLine($"{requestCount}: Completed request");

    requestStream.Close();

    Console.WriteLine($"{requestCount}: Finished with request");
  }
}