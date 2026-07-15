using System;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

// The test assembly exercises this internal pipeline directly (the malformed-200 and
// wrong-shape-200 regression cases point GogHttp at a loopback server).
[assembly: InternalsVisibleTo("Gog.Sdk.Tests")]

namespace Gog.Sdk.Internal
{
    /// <summary>
    /// The one HTTP + JSON-parse pipeline for every C#-side network module
    /// (docs/sdk-api-contract.md §1.4). Its whole reason to exist is that the completion
    /// path is wrapped so that <b>no</b> thrown exception — parse failure included — can
    /// escape without delivering a result. The <c>GogCoins</c> silent hang (a
    /// <c>try/finally</c> with no <c>catch</c> around an unguarded
    /// <c>JsonUtility.FromJson</c>) is structurally impossible here, not individually
    /// patched: there is exactly one place the delivery could be forgotten, and it isn't.
    ///
    /// <list type="bullet">
    /// <item>Callback delivered EXACTLY ONCE, on the Unity main thread (§1.1, I2, I3).</item>
    /// <item>12 s timeout; retry once for idempotent requests on a transient failure (§1.4.2).</item>
    /// <item>Newtonsoft with <c>Required.Always</c> DTO fields: a 200 missing a required
    /// field is a NetworkError naming the field, never a default-filled success (§1.4.5).</item>
    /// <item>Unknown JSON fields ignored (forward tolerance, I9).</item>
    /// </list>
    /// </summary>
    internal static class GogHttp
    {
        private const int TimeoutSeconds = 12; // contract §1.4.2

        /// <summary>Idempotent GET. Retries once on a transient failure. Callback is the
        /// last parameter and is required (§1.1).</summary>
        internal static void Get<T>(string url, string bearer, Action<GogResult<T>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            GogMainThreadDispatcher.Ensure();
            Send("GET", url, bearer, null, retriesLeft: 1, Once(callback));
        }

        /// <summary>POST with a JSON body. <paramref name="idempotent"/> = the body carries
        /// an idempotency key, so a transient retry re-sends the SAME body/key and cannot
        /// double-apply (§1.4.2).</summary>
        internal static void Post<T>(string url, string bearer, string jsonBody, bool idempotent, Action<GogResult<T>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            GogMainThreadDispatcher.Ensure();
            Send("POST", url, bearer, jsonBody, retriesLeft: idempotent ? 1 : 0, Once(callback));
        }

        // Wrap the partner callback so it is delivered EXACTLY ONCE, on the main thread,
        // and a throw FROM the partner is logged — never swallowed, never re-entrant, never
        // escaping into UnityWebRequest's completion machinery (§1.1, C7). `delivered` is
        // only ever touched on the main thread (op.completed + the dispatcher), so no lock.
        private static Action<GogResult<T>> Once<T>(Action<GogResult<T>> callback)
        {
            var delivered = false;
            return result =>
            {
                if (delivered) return;
                delivered = true; // set BEFORE invoking: a throwing callback cannot cause a re-delivery (C7)
                GogMainThreadDispatcher.Enqueue(() =>
                {
                    try { callback(result); }
                    catch (Exception e) { Debug.LogException(e); } // the game's bug — made visible, not hidden (C7)
                });
            };
        }

        private static void Send<T>(string method, string url, string bearer, string jsonBody, int retriesLeft, Action<GogResult<T>> deliver)
        {
            UnityWebRequest req = Build(method, url, bearer, jsonBody);
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                // op.completed fires on the main thread. The ENTIRE body is wrapped: any
                // exception here — including a parse throw from Interpret — becomes a
                // NetworkError delivery, never an escaped exception and never a dropped
                // callback (§1.4.3). This is the structural death of the silent hang.
                try
                {
                    GogResult<T> result = Interpret<T>(req, out bool transient);
                    req.Dispose();
                    if (transient && retriesLeft > 0)
                    {
                        Send(method, url, bearer, jsonBody, retriesLeft - 1, deliver); // same body ⇒ same idempotency key
                        return;
                    }
                    deliver(result);
                }
                catch (Exception e)
                {
                    try { req.Dispose(); } catch { /* already disposed */ }
                    deliver(GogResult<T>.Fail(GogErrorCode.NetworkError,
                        "Response could not be read: " + e.Message, retryable: true, rawReason: e.GetType().Name));
                }
            };
        }

        private static UnityWebRequest Build(string method, string url, string bearer, string jsonBody)
        {
            UnityWebRequest req;
            if (jsonBody == null)
            {
                req = UnityWebRequest.Get(url);
            }
            else
            {
                req = new UnityWebRequest(url, method)
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody)),
                    downloadHandler = new DownloadHandlerBuffer(),
                };
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(bearer))
                req.SetRequestHeader("Authorization", "Bearer " + bearer);
            req.timeout = TimeoutSeconds;
            return req;
        }

        // Turn a completed request into a result. `transient` = worth an automatic retry
        // for an idempotent request (network blip / 5xx), as opposed to a terminal outcome
        // (4xx, or a deterministic malformed body).
        private static GogResult<T> Interpret<T>(UnityWebRequest req, out bool transient)
        {
            transient = false;
            switch (req.result)
            {
                case UnityWebRequest.Result.ConnectionError:
                    transient = true;
                    return GogResult<T>.Fail(GogErrorCode.NetworkError,
                        req.error ?? "connection error", retryable: true, rawReason: "connection_error");
                case UnityWebRequest.Result.DataProcessingError:
                    return GogResult<T>.Fail(GogErrorCode.NetworkError,
                        req.error ?? "data processing error", retryable: true, rawReason: "data_processing_error");
                case UnityWebRequest.Result.ProtocolError:
                    return MapHttpError<T>(req, out transient);
                default: // Success ⇒ 2xx
                    return ParseBody<T>(req);
            }
        }

        private static GogResult<T> ParseBody<T>(UnityWebRequest req)
        {
            string text = req.downloadHandler != null ? req.downloadHandler.text : null;
            T value;
            try
            {
                // Malformed JSON throws JsonReaderException; a MISSING Required.Always field
                // throws JsonSerializationException naming the field. Both are caught here
                // and become NetworkError — never a default-filled "success" (§1.4.5).
                value = JsonConvert.DeserializeObject<T>(text);
            }
            catch (Exception e)
            {
                return GogResult<T>.Fail(GogErrorCode.NetworkError,
                    "Malformed response: " + e.Message, retryable: true, rawReason: "parse_error");
            }
            if (value == null)
                return GogResult<T>.Fail(GogErrorCode.NetworkError,
                    "Empty response body.", retryable: true, rawReason: "empty_body");
            return GogResult<T>.Success(value);
        }

        // The server uses one flat envelope SDK-wide: {"error":"<snake_string>"} (verified
        // against services/api/src/index.ts). Bucket by HTTP status, then let a recognized
        // error string refine it; an unrecognized string degrades to the status bucket.
        private static GogResult<T> MapHttpError<T>(UnityWebRequest req, out bool transient)
        {
            transient = false;
            long status = req.responseCode;
            string serverError = TryParseError(req.downloadHandler != null ? req.downloadHandler.text : null);

            GogErrorCode code;
            bool retryable = false;
            switch (status)
            {
                case 401:
                case 403:
                    code = GogErrorCode.Unauthorized;
                    break;
                case 429:
                    code = GogErrorCode.RateLimited;
                    retryable = true; // caller backs off; we do NOT auto-retry a 429
                    break;
                case 400:
                case 409:
                case 422:
                    code = GogErrorCode.InvalidRequest; // refined by the error string below
                    break;
                default:
                    if (status >= 500) { code = GogErrorCode.InternalError; retryable = true; transient = true; }
                    else code = GogErrorCode.InvalidRequest;
                    break;
            }

            code = Refine(code, serverError);
            return GogResult<T>.Fail(code, serverError ?? ("HTTP " + status), retryable,
                rawReason: serverError ?? ("http_" + status));
        }

        private static GogErrorCode Refine(GogErrorCode fallback, string serverError)
        {
            switch (serverError)
            {
                case "unauthorized":            return GogErrorCode.Unauthorized;
                case "unknown_game":            return GogErrorCode.UnknownGame;
                case "insufficient_funds":      return GogErrorCode.InsufficientFunds;
                case "earn_too_large":          return GogErrorCode.EarnTooLarge;
                case "idempotency_key_reused":  return GogErrorCode.IdempotencyKeyReused;
                case "invalid or expired code": return GogErrorCode.InvalidOrExpiredCode;
                case "internal error":          return GogErrorCode.InternalError;
                case "invalid input":           return GogErrorCode.InvalidRequest;
                default:                        return fallback;
            }
        }

        private static string TryParseError(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try { return JsonConvert.DeserializeObject<ErrorEnvelope>(body)?.Error; }
            catch { return null; } // a non-JSON error body ⇒ fall back to the status bucket
        }

        private sealed class ErrorEnvelope
        {
            [JsonProperty("error")] public string Error { get; set; }
        }
    }
}
