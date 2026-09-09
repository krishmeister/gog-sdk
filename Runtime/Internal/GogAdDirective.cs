using System;
using Newtonsoft.Json;

namespace Gog.Sdk.Internal
{
    /// <summary>What a parsed <c>/ads/next</c> response tells the client to do.</summary>
    internal enum GogAdDirectiveKind
    {
        /// <summary>Render a house creative.</summary>
        House,

        /// <summary>Show nothing. Covers <c>{"type":"none"}</c> and any unknown discriminator
        /// (I9: unknown discriminators map to the module-safe default).</summary>
        NoAd,

        /// <summary>An AdMob directive. Not rendered in this slice — see <c>GogAds</c>.</summary>
        Admob,

        /// <summary>A 200 whose shape does not satisfy the contract. Becomes a NetworkError
        /// naming the offending field, never a default-filled success (§1.4.5).</summary>
        Invalid,
    }

    /// <summary>
    /// The platform-independent half of the ads module: URL construction and the interpretation
    /// of a <c>GET /ads/next</c> response into a directive. Pure functions over plain data — no
    /// <c>UnityEngine</c> types, no platform gate, no network — so the decision logic can be
    /// driven directly by tests on any platform (the Editor reports
    /// <c>RuntimePlatform.WindowsEditor</c>, which the public entry point rejects).
    ///
    /// <para>Wire shapes are frozen by docs/sdk-api-contract.md §6 and verified verbatim against
    /// services/api/src/index.ts (<c>GET /ads/next</c>, lines 560-713):</para>
    /// <code>
    /// {"type":"house","impression_id":"…","creative":{"id":"…","image_url":"https…","click_url":"https…"|null,"media_type":"image"|"video","video_url":"https…"|null,"duration_seconds":15|null},"expires_at":"&lt;ISO-8601&gt;","expires_in":300}
    /// {"type":"admob","impression_id":"…","ad_unit_id":"ca-app-pub-…","expires_at":"&lt;ISO-8601&gt;","expires_in":300}
    /// {"type":"none"}   // HTTP 200 — normal outcome; carries NEITHER expires_at NOR expires_in
    /// </code>
    /// </summary>
    internal sealed class GogAdDirective
    {
        /// <summary>Forced-view default for a static image, in seconds. Mid-band of the approved
        /// 3-5s range: long enough to register the creative, short enough not to feel punitive,
        /// and it splits the range so a later tweak in either direction is a small move.</summary>
        internal const float DefaultImageForcedViewSeconds = 4f;

        /// <summary>Clamp bounds for a server-supplied <c>forced_view_seconds</c>. The clamp is
        /// what makes the field safe to hand to an operator: a typo of 6000 cannot trap a player
        /// in an ad they are not allowed to dismiss.</summary>
        internal const float MinForcedViewSeconds = 1f;

        /// <inheritdoc cref="MinForcedViewSeconds"/>
        internal const float MaxForcedViewSeconds = 30f;

        internal GogAdDirectiveKind Kind { get; private set; }

        /// <summary>House only: the creative's id. Operator-facing content, safe to log.
        /// (Contrast <c>impression_id</c>, which is never logged and never surfaced — I8.)</summary>
        internal string CreativeId { get; private set; }

        /// <summary>House only: the image URL to render.</summary>
        internal string ImageUrl { get; private set; }

        /// <summary>House only: the tap-through target, or null.</summary>
        internal string ClickUrl { get; private set; }

        /// <summary>House only: the video to play, or null for a still-image creative.
        ///
        /// <para>When this is set, <see cref="ImageUrl"/> is the creative's POSTER frame rather
        /// than the ad itself. The poster is still fetched and still rendered — it is what the
        /// player sees while the video prepares, and what they keep seeing if the video cannot
        /// be played at all. A video creative therefore degrades to a working image ad instead
        /// of to nothing, which is the same reason the server sends both (I7).</para></summary>
        internal string VideoUrl { get; private set; }

        /// <summary>House only: the video's length in seconds as the operator recorded it, or 0
        /// when unknown. Advisory — playback is driven by the file and the dismiss window is
        /// still <see cref="ForcedViewSeconds"/>. Parsed so a later slice can use it without a
        /// wire change.</summary>
        internal int DurationSeconds { get; private set; }

        /// <summary>House only: the directive's freshness horizon in seconds, from
        /// <c>expires_in</c>. Enforced on a monotonic clock captured at receipt — never against
        /// <c>expires_at</c>, which is a device-wall-clock value and informational only (§6).</summary>
        internal int ExpiresInSeconds { get; private set; }

        /// <summary>House only: how long the player must view before the close control appears,
        /// already clamped.</summary>
        internal float ForcedViewSeconds { get; private set; }

        /// <summary>Invalid only: the developer-facing reason, naming the offending field.</summary>
        internal string InvalidReason { get; private set; }

        /// <summary>Set only when the server sent a discriminator this build does not know. The
        /// outcome is still <see cref="GogAdDirectiveKind.NoAd"/> (I9); this exists so the caller
        /// can log the forward-evolution case distinctly from a genuine "no ad".</summary>
        internal string UnknownType { get; private set; }

        private GogAdDirective() { }

        /// <summary>
        /// Build the request URL. <paramref name="userId"/> is optional and additive per §6: when
        /// present the decision may also serve city-targeted creatives; when absent only
        /// untargeted ones are eligible. <paramref name="trigger"/> is optional and additive too:
        /// "open" marks the SDK's automatic game-open opportunity, which the server gates on the
        /// game's show_on_open policy. A base URL with a trailing slash is tolerated.
        /// </summary>
        internal static string BuildUrl(string baseUrl, string gameId, string userId, int index,
            string trigger = null)
        {
            string root = (baseUrl ?? string.Empty).TrimEnd('/');
            string url = root + "/ads/next?game_id=" + Escape(gameId) + "&index=" + index.ToString();
            if (!string.IsNullOrEmpty(userId)) url += "&user_id=" + Escape(userId);
            if (!string.IsNullOrEmpty(trigger)) url += "&trigger=" + Escape(trigger);
            return url;
        }

        // Uri.EscapeDataString rather than UnityWebRequest.EscapeURL: this file stays free of
        // UnityEngine so it is drivable from any test context. Both values are UUIDs in practice;
        // escaping is hygiene, not a fix for a known input.
        private static string Escape(string value) =>
            string.IsNullOrEmpty(value) ? string.Empty : Uri.EscapeDataString(value);

        /// <summary>
        /// Interpret a parsed response. The union is discriminated on <c>type</c>, which
        /// Newtonsoft cannot branch on without a custom converter — so <see cref="AdsNextDto"/>
        /// is one flat DTO and the per-shape required-field checks live here. A violation becomes
        /// <see cref="GogAdDirectiveKind.Invalid"/> carrying the field name, preserving §1.4.5's
        /// guarantee that a wrong-shape 200 is a failure and never a default-filled success.
        /// </summary>
        internal static GogAdDirective Interpret(AdsNextDto dto)
        {
            if (dto == null) return Invalid("empty response body");

            switch (dto.Type)
            {
                case "none":
                    return new GogAdDirective { Kind = GogAdDirectiveKind.NoAd };

                case "admob":
                    // Deliberately unvalidated: this slice resolves every admob directive to NoAd
                    // (I7), so validating fields on a directive we are about to discard would
                    // manufacture spurious NetworkErrors for a working server.
                    return new GogAdDirective { Kind = GogAdDirectiveKind.Admob };

                case "house":
                    if (dto.Creative == null)
                        return Invalid("house directive missing required field 'creative'");
                    if (!dto.ExpiresIn.HasValue)
                        return Invalid("house directive missing required field 'expires_in'");
                    // A creative counts as video only when it says so AND carries a source.
                    // "video with no video_url" is treated as a plain image rather than as
                    // Invalid: the poster is a perfectly serveable ad, and refusing to show it
                    // would turn a server-side data problem into a blank screen (I7).
                    bool isVideo =
                        string.Equals(dto.Creative.MediaType, "video", StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(dto.Creative.VideoUrl);

                    return new GogAdDirective
                    {
                        Kind = GogAdDirectiveKind.House,
                        CreativeId = dto.Creative.Id,
                        ImageUrl = dto.Creative.ImageUrl,
                        ClickUrl = dto.Creative.ClickUrl,
                        VideoUrl = isVideo ? dto.Creative.VideoUrl : null,
                        DurationSeconds = dto.Creative.DurationSeconds ?? 0,
                        ExpiresInSeconds = dto.ExpiresIn.Value,
                        ForcedViewSeconds = ResolveForcedView(dto.Creative.ForcedViewSeconds),
                    };

                default:
                    // I9 / §1.4.6 — an unknown discriminator maps to the module-safe default.
                    // This is designed forward-evolution (a future "rewarded" type), not a fault.
                    return new GogAdDirective
                    {
                        Kind = GogAdDirectiveKind.NoAd,
                        UnknownType = string.IsNullOrEmpty(dto.Type) ? "(missing)" : dto.Type,
                    };
            }
        }

        /// <summary>
        /// Absent → the compiled default; present → the server wins, clamped. Parsed NOW even
        /// though no server sends it yet, so making the forced-view window dashboard-controllable
        /// is a pure backend change later with ZERO SDK release — exactly the §8 additive rule.
        /// </summary>
        private static float ResolveForcedView(int? serverValue)
        {
            if (!serverValue.HasValue) return DefaultImageForcedViewSeconds;
            float v = serverValue.Value;
            if (v < MinForcedViewSeconds) return MinForcedViewSeconds;
            if (v > MaxForcedViewSeconds) return MaxForcedViewSeconds;
            return v;
        }

        private static GogAdDirective Invalid(string reason) =>
            new GogAdDirective { Kind = GogAdDirectiveKind.Invalid, InvalidReason = reason };
    }

    /// <summary>
    /// The flat wire DTO for <c>GET /ads/next</c>. Only <c>type</c> is <c>Required.Always</c> —
    /// the per-shape requirements are enforced by <see cref="GogAdDirective.Interpret"/> because
    /// <c>Required</c> cannot express "creative is required iff type == house".
    ///
    /// <para>Unknown fields are ignored by Newtonsoft's default MissingMemberHandling, which is
    /// the client half of the server's additive-only evolution rule (I9) — no configuration
    /// needed.</para>
    /// </summary>
    internal sealed class AdsNextDto
    {
        [JsonProperty("type", Required = Required.Always)] public string Type { get; set; }

        /// <summary>
        /// Held internally and NEVER surfaced: I8 keeps telemetry keys off the public surface, and
        /// §2 lists impression_id under "deliberately absent". It is parsed and discarded in this
        /// slice; v1.1 reporting consumes it from inside the SDK, so partners get impression and
        /// click reporting from a pure version bump. It is also never written to a log line.
        /// </summary>
        [JsonProperty("impression_id")] public string ImpressionId { get; set; }

        [JsonProperty("creative")] public AdsNextCreativeDto Creative { get; set; }

        /// <summary>Admob only. Unused this slice — the render lands in S8b-2.</summary>
        [JsonProperty("ad_unit_id")] public string AdUnitId { get; set; }

        /// <summary>Seconds. Absent on <c>{"type":"none"}</c> by server design, hence nullable.</summary>
        [JsonProperty("expires_in")] public int? ExpiresIn { get; set; }

        /// <summary>ISO-8601. Informational only — logs and debugging. NEVER the enforcement
        /// clock: it is a device-wall-clock comparison and device clocks lie (§6).</summary>
        [JsonProperty("expires_at")] public string ExpiresAt { get; set; }
    }

    /// <summary>
    /// The house creative. <c>id</c> and <c>image_url</c> are <c>Required.Always</c>, so a
    /// malformed creative object fails inside Newtonsoft naming the field. <c>click_url</c> is
    /// nullable by server design.
    /// </summary>
    internal sealed class AdsNextCreativeDto
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }

        [JsonProperty("image_url", Required = Required.Always)] public string ImageUrl { get; set; }

        [JsonProperty("click_url")] public string ClickUrl { get; set; }

        /// <summary>Forward hook: no server sends this yet. Absent → the compiled default;
        /// present → the server wins, clamped to 1-30s.</summary>
        [JsonProperty("forced_view_seconds")] public int? ForcedViewSeconds { get; set; }

        /// <summary>"image" or "video". ABSENT on servers that predate video, which is why it is
        /// optional and why absent means image — the field was added additively and old builds
        /// simply never saw it.</summary>
        [JsonProperty("media_type")] public string MediaType { get; set; }

        /// <summary>The video source. Present only alongside media_type "video".</summary>
        [JsonProperty("video_url")] public string VideoUrl { get; set; }

        /// <summary>Operator-recorded video length in seconds. Advisory.</summary>
        [JsonProperty("duration_seconds")] public int? DurationSeconds { get; set; }
    }
}
