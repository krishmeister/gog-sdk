using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Gog.Sdk;
using Gog.Sdk.Internal;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gog.Sdk.Tests
{
    /// <summary>
    /// The GogHttp contract, proven on the metal (docs/sdk-api-contract.md §1.4, §7.4/§7.5).
    /// Every case points the pipeline at a hermetic loopback server and asserts the callback
    /// fires exactly once, on the main thread, with the right taxonomy — the standing proof
    /// that the GogCoins silent-hang and the wrong-shape-200 default-fill cannot come back.
    /// </summary>
    public class GogHttpTests
    {
        private sealed class BalanceDto
        {
            [JsonProperty("balance", Required = Required.Always)] public long Balance { get; set; }
        }

        private LoopbackServer _server;
        private int _mainThreadId;

        [SetUp]
        public void SetUp() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            _server = null;
        }

        // ── THE headline regression: a well-formed 200 with an unparseable body must reach
        //    the callback as NetworkError, on the main thread, once — never hang (§7.4). ──
        [UnityTest]
        public IEnumerator MalformedTwoHundred_DeliversNetworkError_NotAHang()
        {
            _server = LoopbackServer.Constant(200, "application/json", "<html>not json</html>");
            GogResult<BalanceDto> result = default;
            bool done = false;
            int calls = 0;
            int callbackThread = -1;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r =>
            {
                calls++;
                result = r;
                callbackThread = Thread.CurrentThread.ManagedThreadId;
                done = true;
            });

            yield return WaitFor(() => done, 5f, "callback never fired within 5s — THE SILENT HANG is back");
            Assert.AreEqual(1, calls, "callback must fire exactly once");
            Assert.AreEqual(_mainThreadId, callbackThread, "callback must be delivered on the main thread");
            Assert.IsFalse(result.Ok, "a malformed 200 must be a failure, not a success");
            Assert.AreEqual(GogErrorCode.NetworkError, result.Error.Code);
        }

        // ── The wrong-success bug: valid JSON, wrong shape (required field missing) must be a
        //    failure that NAMES the field — not a default-filled balance = 0 success (§7.5). ──
        [UnityTest]
        public IEnumerator WrongShapeTwoHundred_FailsNamingField_NotDefaultZero()
        {
            _server = LoopbackServer.Constant(200, "application/json", "{\"foo\":1}");
            GogResult<BalanceDto> result = default;
            bool done = false;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r => { result = r; done = true; });

            yield return WaitFor(() => done, 5f, "callback never fired");
            Assert.IsFalse(result.Ok, "a required field missing must NOT be a default-filled success");
            Assert.AreEqual(GogErrorCode.NetworkError, result.Error.Code);
            StringAssert.Contains("balance", result.Error.Message,
                "the failure should name the missing required field");
        }

        [UnityTest]
        public IEnumerator ConnectionRefused_DeliversNetworkError()
        {
            int deadPort = LoopbackServer.ReserveClosedPort();
            GogResult<BalanceDto> result = default;
            bool done = false;

            GogHttp.Get<BalanceDto>($"http://127.0.0.1:{deadPort}/", null, r => { result = r; done = true; });

            yield return WaitFor(() => done, 5f, "callback never fired");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.NetworkError, result.Error.Code);
        }

        [UnityTest]
        public IEnumerator Unauthorized401_MapsToUnauthorized()
        {
            _server = LoopbackServer.Constant(401, "application/json", "{\"error\":\"unauthorized\"}");
            GogResult<BalanceDto> result = default;
            bool done = false;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r => { result = r; done = true; });

            yield return WaitFor(() => done, 5f, "callback never fired");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.Unauthorized, result.Error.Code);
        }

        [UnityTest]
        public IEnumerator RateLimited429_MapsToRateLimited()
        {
            _server = LoopbackServer.Constant(429, "application/json", "{\"error\":\"rate_limited\"}");
            GogResult<BalanceDto> result = default;
            bool done = false;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r => { result = r; done = true; });

            yield return WaitFor(() => done, 5f, "callback never fired");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.RateLimited, result.Error.Code);
        }

        [UnityTest]
        public IEnumerator ValidTwoHundred_DeliversOk()
        {
            _server = LoopbackServer.Constant(200, "application/json", "{\"balance\":42}");
            GogResult<BalanceDto> result = default;
            bool done = false;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r => { result = r; done = true; });

            yield return WaitFor(() => done, 5f, "callback never fired");
            Assert.IsTrue(result.Ok, "a valid 200 should succeed");
            Assert.AreEqual(42, result.Value.Balance);
        }

        // ── Retry-once for an idempotent GET: a transient 503 then 200 → Ok, delivered
        //    exactly once (§1.4.2). Proves the retry never double-delivers. ──
        [UnityTest]
        public IEnumerator TransientFiveHundredThenOk_RetriesOnce_DeliversOnce()
        {
            int hits = 0;
            _server = new LoopbackServer(() =>
            {
                int n = Interlocked.Increment(ref hits);
                return n == 1
                    ? new LoopbackServer.Response(503, "application/json", "{\"error\":\"internal error\"}")
                    : new LoopbackServer.Response(200, "application/json", "{\"balance\":7}");
            });
            GogResult<BalanceDto> result = default;
            int calls = 0;
            bool done = false;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r => { calls++; result = r; done = true; });

            yield return WaitFor(() => done, 5f, "callback never fired");
            yield return null; // let any erroneous second delivery surface
            yield return null;
            Assert.AreEqual(1, calls, "must deliver exactly once even across a retry");
            Assert.IsTrue(result.Ok, "should succeed on the retry");
            Assert.AreEqual(7, result.Value.Balance);
            Assert.GreaterOrEqual(hits, 2, "server should have been hit twice (original + retry)");
        }

        // ── C7: a throwing partner callback is logged (not swallowed), is not re-delivered,
        //    and does not corrupt the SDK — a later request still works. ──
        [UnityTest]
        public IEnumerator PartnerCallbackThrows_IsLogged_NoReDelivery_StateIntact()
        {
            LogAssert.Expect(LogType.Exception, new Regex("boom"));
            _server = LoopbackServer.Constant(200, "application/json", "{\"balance\":1}");
            int calls = 0;
            bool done = false;

            GogHttp.Get<BalanceDto>(_server.Url(), null, r =>
            {
                calls++;
                done = true;
                throw new Exception("boom");
            });

            yield return WaitFor(() => done, 5f, "callback never fired");
            yield return null;
            yield return null;
            Assert.AreEqual(1, calls, "a throwing callback must not be re-delivered");

            // The SDK still works after a partner threw.
            _server.Dispose();
            _server = LoopbackServer.Constant(200, "application/json", "{\"balance\":99}");
            GogResult<BalanceDto> after = default;
            bool done2 = false;
            GogHttp.Get<BalanceDto>(_server.Url(), null, r => { after = r; done2 = true; });

            yield return WaitFor(() => done2, 5f, "SDK did not recover after a partner threw");
            Assert.IsTrue(after.Ok);
            Assert.AreEqual(99, after.Value.Balance);
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, string failMessage)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), failMessage);
        }
    }

    /// <summary>
    /// A minimal, hermetic loopback HTTP/1.1 server over a raw <see cref="TcpListener"/> on
    /// 127.0.0.1 (an ephemeral port). Deliberately NOT <c>HttpListener</c>: that needs an
    /// http.sys URL ACL on Windows and would fail a non-admin batchmode run. Each accepted
    /// connection gets one response from the supplied responder, then closes.
    /// </summary>
    internal sealed class LoopbackServer : IDisposable
    {
        public readonly struct Response
        {
            public readonly int Status;
            public readonly string ContentType;
            public readonly string Body;

            /// <summary>Raw body, for non-text payloads (e.g. a PNG). Wins over
            /// <see cref="Body"/> when non-null.</summary>
            public readonly byte[] BodyBytes;

            public Response(int status, string contentType, string body)
            {
                Status = status;
                ContentType = contentType;
                Body = body;
                BodyBytes = null;
            }

            public Response(int status, string contentType, byte[] bodyBytes)
            {
                Status = status;
                ContentType = contentType;
                Body = null;
                BodyBytes = bodyBytes;
            }
        }

        private readonly TcpListener _listener;
        private readonly Func<string, Response> _responder;
        private readonly List<string> _requests = new List<string>();
        private readonly object _requestsGate = new object();
        private volatile bool _running;

        public int Port { get; }

        /// <summary>Path-aware responder: receives the request target ("/ads/next?game_id=…"), so
        /// one server can serve both a directive and the creative it points at.</summary>
        public LoopbackServer(Func<string, Response> responder)
        {
            _responder = responder;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            new Thread(Loop) { IsBackground = true, Name = "GogLoopbackServer" }.Start();
        }

        public LoopbackServer(Func<Response> responder) : this(_ => responder()) { }

        public static LoopbackServer Constant(int status, string contentType, string body)
            => new LoopbackServer(() => new Response(status, contentType, body));

        /// <summary>Every request target this server has been asked for, in order. Lets a test
        /// assert what the SDK actually SENT — the impression index in particular.</summary>
        public IReadOnlyList<string> Requests
        {
            get { lock (_requestsGate) { return _requests.ToArray(); } }
        }

        public int RequestCount
        {
            get { lock (_requestsGate) { return _requests.Count; } }
        }

        public string Url(string path = "/") => $"http://127.0.0.1:{Port}{path}";

        /// <summary>Reserve an ephemeral port and release it immediately, so a connection
        /// there is refused rather than accepted.</summary>
        public static int ReserveClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private void Loop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped

                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        string target = DrainRequest(stream);
                        lock (_requestsGate) { _requests.Add(target ?? string.Empty); }
                        Response resp = _responder(target ?? string.Empty);
                        byte[] bodyBytes = resp.BodyBytes ?? Encoding.UTF8.GetBytes(resp.Body ?? string.Empty);
                        string head =
                            $"HTTP/1.1 {resp.Status} {ReasonPhrase(resp.Status)}\r\n" +
                            $"Content-Type: {resp.ContentType}\r\n" +
                            $"Content-Length: {bodyBytes.Length}\r\n" +
                            "Connection: close\r\n\r\n";
                        byte[] headBytes = Encoding.ASCII.GetBytes(head);
                        stream.Write(headBytes, 0, headBytes.Length);
                        stream.Write(bodyBytes, 0, bodyBytes.Length);
                        stream.Flush();
                    }
                }
                catch { /* client vanished — ignore */ }
            }
        }

        /// <summary>Read the request headers and return the request target from the request line
        /// ("GET /ads/next?index=3 HTTP/1.1" → "/ads/next?index=3"), or null.</summary>
        private static string DrainRequest(NetworkStream stream)
        {
            try
            {
                stream.ReadTimeout = 1500;
                var sb = new StringBuilder();
                var buf = new byte[1024];
                while (sb.Length < 65536)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("\r\n\r\n")) break; // end of request headers
                }

                string head = sb.ToString();
                int eol = head.IndexOf("\r\n", StringComparison.Ordinal);
                string requestLine = eol >= 0 ? head.Substring(0, eol) : head;
                string[] parts = requestLine.Split(' ');
                return parts.Length >= 2 ? parts[1] : null;
            }
            catch { return null; } // best-effort: the response does not depend on the request body
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }
    }
}
