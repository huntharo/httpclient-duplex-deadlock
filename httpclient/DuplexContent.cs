using System.Net;

/// <summary>
/// Borrowed from tests in dotnet/runtime/src/libraries/System.Net.Http/tests/FunctionalTests/HttpClientHandlerTest.Http2.cs
/// 
/// We have to derive from HttpContent since no offered derived class
/// has AllowDuplex set to true, only the base class does.
/// </summary>
public class HttpDuplexContent : HttpContent
{
  private readonly TaskCompletionSource<Stream> _waitForStream;
  private TaskCompletionSource? _waitForCompletion;

  public HttpDuplexContent()
  {
    _waitForStream = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  protected override bool TryComputeLength(out long length)
  {
    length = -1;
    return false;
  }

  protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
  {
    _waitForCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _waitForStream.SetResult(stream);
    await _waitForCompletion.Task;
  }

  public Task<Stream> WaitForStreamAsync()
  {
    return _waitForStream.Task;
  }

  public void Complete()
  {
    _waitForCompletion?.SetResult();
  }

  public void Fail(Exception e)
  {
    _waitForCompletion?.SetException(e);
  }
}
