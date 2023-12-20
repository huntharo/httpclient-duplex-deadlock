# Overview

This is an SSCCE (Short, Self-Contained, Correct (Compilable), Example)[http://www.sscce.org/] of a deadlock in (DotNet Core 8)[https://github.com/dotnet/runtime] when using a custom `Content` object with `AllowDuplex=true` over HTTP2.

The problem is that `HttpClient` in (Http2Connection.cs/SendHeadersAsync)[] will not force a flush to write new headers on the entire connection (not just the stream) except in these cases:
1. There is a half-duplex request on the same HTTP2 connection (half-duplex meaning the entire Request Content data is available before the request is sent)
   1.  This happens because s.EndStream is true for half-duplex requests: https://github.com/dotnet/runtime/blob/2f1fbb009cccc137092429e2a7f367ad7bed92b9/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Connection.cs#L1684
2. All pending streams have enough data force a flush of the headers (e.g. there are 10 paralell requests and they all send a lot of headers)'
3. `Headers.ExpectContinue` is `true`, which causes `mustFlush` to be set to true here: https://github.com/dotnet/runtime/blob/2f1fbb009cccc137092429e2a7f367ad7bed92b9/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Connection.cs#L1999

Other than this issue, `AllowDuplex` works fine on HTTP2.

## Video of Issue and Fix

[![Reproduction of the deadlock issue in HttpClient and demonstration of possible fixes](https://img.youtube.com/vi/UN2oHXhKQf0/0.jpg)](https://www.youtube.com/watch?v=UN2oHXhKQf0)

## Related Concern

First, it is ok / wise to batch up requests before sending them to the server.  But, there needs to be a periodic flush to clear any pending data.  It does not seem acceptable to leave data in a send buffer indefinitely, which is the case here.

If there is no background flush timer then there may be other related fixes needed.

Since the behavior here is changed by the other requests sent on the same connection, and no existing tests have caught this, it seems like there may be other related issues or that the tests may need to be updated to ensure complete isolation of connections across tests.

## Fix Approach

Possibly the simplest fix is that the callback in `SendHeadersAsync` should *always* return `true`, causing a flush.  The nominal case of non-duplex requests essentially always causes this to return `true` and it seems only this odd case of duplex requests is causing it to return `false`.
https://github.com/dotnet/runtime/blob/2f1fbb009cccc137092429e2a7f367ad7bed92b9/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Connection.cs#L1684

However, a more narrow fix is to add in `mustFlush: ... || AllowDuplex` here: https://github.com/dotnet/runtime/blob/2f1fbb009cccc137092429e2a7f367ad7bed92b9/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Connection.cs#L1999

## Repro Steps

1. `./certs.sh`
   1. This generates a self-signed cert but does not trust it
   2. The test HttpClient has a custom `ServerCertificateCustomValidationCallback` that trusts the cert
2. `dotnet restore`
3. Install and setup WireShark
   1. Open WireShark
   2. Open Preferences
   3. Select Protocols
   4. Select the TLS protocol
   5. Click the `RSA keys list` Edit... button
   6. Click the + button
      1. Enter address `127.0.0.1`
      2. Enter port `5050`
      3. Enter protocol `http`
      4. Browse and find `httpclient-duplex-deadlock/certs/dummy.local.key`
   7. Start capturing traffic on the loopback interface
   8. Add a filter for `tcp.port == 5050`
4. Open 2 terminals
5. In terminal 1:\
   1. `dotnet run --project server`
   2. This can be left running for all of the test variations below
6. In terminal 2:
   1. `TEST_MODE=complete-deadlock-one-request dotnet run --project httpclient`
      1. This will hang until the connection times out
      2. Wireshark will show the `SETTINGS` and `WINDOW_UPDATE` frames exchanged, but no `HEADERS` frame sent
      3. Simple adding a `Date` request header will cause the 1st request to send, but the 2nd request will deadlock
   2. `TEST_MODE=complete-deadlock-many-requests dotnet run --project httpclient`
      1. This will hang until the connection times out
      2. The 1st 10 parallel requests will send because they have enough data to force a flush with the connection establishment, but the subsequent 10 parallel requests will not have enough data to force a flush and will never send their `HEADERS` frame
   3. `TEST_MODE=partial-deadlock-many-requests dotnet run --project httpclient`
      1. This finishes in ~10 seconds
      2. This works because the 10 parallel requests are *always* sending a lot of headers, which causes a flush
      3. However, the requests would deadlock if sent individually, as below
   4. `TEST_MODE=complete-deadlock-one-request-with-headers dotnet run --project httpclient`
      1. This will hang until the connection times out
      2. This sends the same mass of headers as in `partial-deadlock-many-requests`, but only sends 1 request, which is not sufficient by itself to cause a flush
   5. `TEST_MODE=partial-deadlock-with-half-duplex-requests dotnet run --project httpclient`
      1. This finishes in `~50` seconds!!!
      2. This "works" because there is a once-per-5-seconds half-duplex request that causes a flush when it's headers send, which forces a flush of all pending frames in front of that
      3. The 50 seconds vs 10 seconds shows the impact of the 5 second delay between the half-duplex requests

## Testing HttpClient Fix Locally

1. Open ()[./httpclient/httpclient.csproj]
2. Uncomment `<EnablePreviewFeatures>true</EnablePreviewFeatures>`
3. Uncomment the `<Reference Include="System.Net.Http">` `ItemGroup` block
4. Fix the path to point to your local release build of `System.Net.Http.dll`
5. Checkout `dotnet/runtime` and build it
   1. Switch to the `release/8.0` branch
   2. `dotnet build -c Release` once at the repo root
   3. Check if `artifacts/bin/System.Net.Http/Release/net8.0-osx` (for example) has a `System.Net.Http.dll` file
   4. If the file exists, you can proceed to building/rebuilding only in `src/libraries/System.Net.Http`
6. Run rebuilds only in `src/libraries/System.Net.Http`
   1. `cd src/libraries/System.Net.Http`
   2. `dotnet build -c Release`
   3. Confirm that the timestamp on `System.Net.Http.dll` in `artifacts/bin/System.Net.Http/Release/net8.0-osx` has been updated
7. If you want to be able to set breakpoints in `System.Net.Http` code:
   1. Open `src/libraries/System.Net.Http/src/System.Net.Http.csproj`
   2. Add `<Optimize>false</Optimize>` to the top `<PropertyGroup>`
   3. Rebuild `src/libraries/System.Net.Http`
8. Set breakpoints in this project for files in `src/libraries/System.Net.Http`
   1. Copy the absolute path to `src/System/Net/Http/SocketsHttpHandler/Http2Connection.cs`
   2. In VS Code, hit Command-P and paste in the path to open the file
   3. Set a breakpoint on the first line of `SendHeadersAsync`
   4. Start the `httpclient` project using the debug configuration `HttpClient (console)`
   5. Confirm that the breakpoint in `SendHeadersAsync` is red (if it's gray the code was changed but the library was not recompiled, or disabling optimization was missed)
