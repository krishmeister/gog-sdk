namespace Gog.Sdk
{
    /// <summary>
    /// The single error taxonomy for the whole SDK (docs/sdk-api-contract.md §1.2/§2/§5).
    /// Every module maps its failures onto these codes — there is no second enum. New
    /// modules ADD members (additive); consumers must default-branch unknown values.
    /// </summary>
    public enum GogErrorCode
    {
        // common
        NotInitialized, InvalidRequest, NetworkError, Unauthorized, InternalError,
        UnsupportedPlatform, UnsupportedOsVersion, RateLimited, UnknownGame,
        // auth
        InvalidOrExpiredCode, SignInCancelled, HubNotInstalled, HubVerificationFailed, SignInInProgress,
        // coins
        InsufficientFunds, EarnTooLarge, IdempotencyKeyReused,
        // ads — "no ad" itself stays a SUCCESS (GogAdOutcome.NoAd), never an error; the ONE
        // ad code is the missing-app-id integration bug (contract §5, I7). No producer yet —
        // S8b's GogAds wires it (RawReason "no_app_id"); a missing GMA package is NOT this
        // code (that resolves to NoAd per I7).
        AdNotConfigured,
    }

    /// <summary>
    /// One error type for the whole SDK. <see cref="Retryable"/> is set PER OCCURRENCE by
    /// the producer, not looked up from <see cref="Code"/>: the same
    /// <see cref="GogErrorCode.NetworkError"/> is non-retryable on a burned sign-in code
    /// but retryable on a balance fetch (contract §1.2).
    ///
    /// <para>A value type, exactly as contract §1.2/§2 declare (readonly struct — the
    /// success path allocates nothing, which matters on the ads impression path; the
    /// contract absorbed this in cd7e3a8, so it is no longer a divergence). Because it is a
    /// struct, <c>default(GogError)</c> is reachable; <see cref="Message"/> is null-safe so
    /// the contract's "never null" guarantee holds even for a defaulted value.</para>
    /// </summary>
    public readonly struct GogError
    {
        private readonly string _message;

        /// <summary>The taxonomy bucket. Consumers should default-branch unknown values.</summary>
        public GogErrorCode Code { get; }

        /// <summary>Developer-facing detail. Never null (a defaulted GogError yields a
        /// non-null fallback rather than the enum's zero value masquerading as truth).</summary>
        public string Message => _message ?? "(unspecified error)";

        /// <summary>Whether retrying THIS occurrence is sensible. Context-aware — see the
        /// class summary and contract §5.</summary>
        public bool Retryable { get; }

        /// <summary>The underlying transport/JNI reason string (e.g. <c>hub_untrusted</c>,
        /// <c>parse_error</c>, <c>http_409</c>). Diagnostics only — may be null; never
        /// branch player-facing logic on it. An addition beyond §2, approved for triage.</summary>
        public string RawReason { get; }

        internal GogError(GogErrorCode code, string message, bool retryable, string rawReason = null)
        {
            Code = code;
            _message = message;
            Retryable = retryable;
            RawReason = rawReason;
        }

        public override string ToString() =>
            RawReason == null ? $"[{Code}] {Message}" : $"[{Code}] {Message} (raw: {RawReason})";
    }

    /// <summary>
    /// The single result type every async SDK operation completes with (contract §1.1/§2).
    /// Exactly one of <see cref="Value"/> / <see cref="Error"/> is meaningful, selected by
    /// <see cref="Ok"/>.
    ///
    /// <para>The success property is named <c>Ok</c> to match the frozen surface (§2). The
    /// construction factories are therefore <see cref="Success"/> / <see cref="Fail"/>, NOT
    /// <c>Ok</c> / <c>Fail</c> as the S7.1 plan proposed: a static <c>Ok(...)</c> factory
    /// would collide with the <c>Ok</c> property (a C# compile error — a type cannot have a
    /// method and a property of the same name). The factories are <c>internal</c>, so they
    /// are not part of the public surface either way.</para>
    /// </summary>
    public readonly struct GogResult<T>
    {
        /// <summary>True on success; then <see cref="Value"/> is valid. False on failure;
        /// then <see cref="Error"/> is valid.</summary>
        public bool Ok { get; }
        public T Value { get; }
        public GogError Error { get; }

        private GogResult(bool ok, T value, GogError error)
        {
            Ok = ok;
            Value = value;
            Error = error;
        }

        internal static GogResult<T> Success(T value) => new GogResult<T>(true, value, default);
        internal static GogResult<T> Fail(GogError error) => new GogResult<T>(false, default, error);
        internal static GogResult<T> Fail(GogErrorCode code, string message, bool retryable, string rawReason = null)
            => new GogResult<T>(false, default, new GogError(code, message, retryable, rawReason));
    }
}
