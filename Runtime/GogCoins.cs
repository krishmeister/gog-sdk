using System;
using Gog.Sdk.Internal;
using Newtonsoft.Json;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>A coin balance snapshot (GET /me/wallet).</summary>
    public sealed class GogCoinBalance
    {
        public long Balance { get; }
        public GogCoinBalance(long balance) { Balance = balance; }
    }

    /// <summary>The outcome of an earn/spend. <see cref="Applied"/> is false when the
    /// call was an idempotent replay (the original transaction is returned unchanged).
    /// (Renamed from GogCoinResult to avoid collision with the GogResult&lt;T&gt; concept.)</summary>
    public sealed class GogCoinTransaction
    {
        public long Balance { get; }
        public string TransactionId { get; }
        public bool Applied { get; }

        public GogCoinTransaction(long balance, string transactionId, bool applied)
        {
            Balance = balance;
            TransactionId = transactionId;
            Applied = applied;
        }
    }

    /// <summary>
    /// Coins surface — a thin facade over the wallet API. Every operation takes a single
    /// required <see cref="Action{T}"/> of <see cref="GogResult{T}"/> (contract §1.1) and
    /// routes through the one <c>GogHttp</c> pipeline (§1.4, §7.3) — no raw
    /// <c>UnityWebRequest</c>, no bespoke parse, no bespoke error enum. game_id and the API
    /// base URL come from the single <see cref="GogSettings"/> asset via <see cref="GogSdk"/>;
    /// games never pass them per call.
    /// </summary>
    public static class GogCoins
    {
        /// <summary>GET /me/wallet — the signed-in user's coin balance (0 if none yet).</summary>
        public static void GetBalance(Action<GogResult<GogCoinBalance>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!Precheck(callback, out string baseUrl, out _)) return;
            GogAuth.GetAccessToken(tok =>
            {
                if (!tok.Ok) { callback(GogResult<GogCoinBalance>.Fail(tok.Error)); return; }
                GogHttp.Get<BalanceDto>(baseUrl + "/me/wallet", tok.Value,
                    r => callback(Project(r, d => new GogCoinBalance(d.Balance))));
            });
        }

        /// <summary>POST /wallet/earn — grant coins. The SDK generates a retry-safe
        /// idempotency key for you.</summary>
        public static void Earn(long amount, string reason, Action<GogResult<GogCoinTransaction>> callback)
            => Apply("/wallet/earn", amount, reason, NewKey(), callback);

        /// <summary>POST /wallet/earn with a caller-supplied idempotency key (retry-safe).</summary>
        public static void Earn(long amount, string reason, string idempotencyKey, Action<GogResult<GogCoinTransaction>> callback)
            => Apply("/wallet/earn", amount, reason, idempotencyKey, callback);

        /// <summary>POST /wallet/spend — debit coins. The SDK generates a retry-safe
        /// idempotency key for you.</summary>
        public static void Spend(long amount, string reason, Action<GogResult<GogCoinTransaction>> callback)
            => Apply("/wallet/spend", amount, reason, NewKey(), callback);

        /// <summary>POST /wallet/spend with a caller-supplied idempotency key (retry-safe).</summary>
        public static void Spend(long amount, string reason, string idempotencyKey, Action<GogResult<GogCoinTransaction>> callback)
            => Apply("/wallet/spend", amount, reason, idempotencyKey, callback);

        // ── internals ──────────────────────────────────────────────────────────

        private static void Apply(string path, long amount, string reason, string idempotencyKey,
            Action<GogResult<GogCoinTransaction>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!Precheck(callback, out string baseUrl, out string gameId)) return;

            string body = JsonConvert.SerializeObject(new ApplyReq
            {
                GameId = gameId,
                Amount = amount,
                IdempotencyKey = idempotencyKey,
                Reason = reason ?? string.Empty,
            });

            GogAuth.GetAccessToken(tok =>
            {
                if (!tok.Ok) { callback(GogResult<GogCoinTransaction>.Fail(tok.Error)); return; }
                // idempotent: true ⇒ GogHttp retries once on a transient failure, re-sending
                // the SAME body (same key) — a retried earn/spend can never double-apply.
                GogHttp.Post<ApplyDto>(baseUrl + path, tok.Value, body, idempotent: true,
                    r => callback(Project(r, d => new GogCoinTransaction(d.Balance, d.TransactionId, d.Applied))));
            });
        }

        // Pre-flight the states that must NOT hit the network. NotInitialized is checked
        // FIRST so a pre-init call is NotInitialized on any platform — including the Editor —
        // rather than UnsupportedPlatform (contract §4/I4). Config comes from the one
        // GogSettings asset via GogSdk; there is no per-call or second config source.
        private static bool Precheck<T>(Action<GogResult<T>> callback, out string baseUrl, out string gameId)
        {
            baseUrl = null;
            gameId = null;
            if (!GogSdk.IsInitialized)
            {
                callback(GogResult<T>.Fail(GogErrorCode.NotInitialized, "GogSdk.Initialize() was not called.", retryable: false));
                return false;
            }
            if (Application.platform != RuntimePlatform.Android)
            {
                callback(GogResult<T>.Fail(GogErrorCode.UnsupportedPlatform, "GOG Coins runs on Android only.", retryable: false));
                return false;
            }
            if (!GogAuth.CurrentSession.IsSignedIn)
            {
                callback(GogResult<T>.Fail(GogErrorCode.Unauthorized, "Not signed in.", retryable: false));
                return false;
            }
            var settings = GogSdk.Settings;
            baseUrl = settings.ApiBaseUrl;
            gameId = settings.GameId;
            return true;
        }

        private static GogResult<TOut> Project<TIn, TOut>(GogResult<TIn> r, Func<TIn, TOut> map)
            => r.Ok ? GogResult<TOut>.Success(map(r.Value)) : GogResult<TOut>.Fail(r.Error);

        private static string NewKey() => Guid.NewGuid().ToString();

        // ── wire DTOs (Newtonsoft; required fields fail a wrong-shape 200 rather than
        //    default-filling — contract §1.4.5). Verified against services/api/src/index.ts. ──
        private sealed class ApplyReq
        {
            [JsonProperty("game_id")] public string GameId { get; set; }
            [JsonProperty("amount")] public long Amount { get; set; }
            [JsonProperty("idempotency_key")] public string IdempotencyKey { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
        }

        private sealed class BalanceDto
        {
            [JsonProperty("balance", Required = Required.Always)] public long Balance { get; set; }
        }

        private sealed class ApplyDto
        {
            [JsonProperty("balance", Required = Required.Always)] public long Balance { get; set; }
            [JsonProperty("transaction_id", Required = Required.Always)] public string TransactionId { get; set; }
            [JsonProperty("applied", Required = Required.Always)] public bool Applied { get; set; }
        }
    }
}
