namespace Gog.Sdk
{
    /// <summary>
    /// Read-only session view exposed to game code. Carries identity only —
    /// there is deliberately NO token property here. The single way to obtain an
    /// access token is <see cref="GogAuth.GetAccessToken"/>; the refresh token is
    /// never exposed to managed code at all.
    /// </summary>
    public sealed class GogSession
    {
        public bool IsSignedIn { get; internal set; }
        public GogUser User { get; internal set; }
    }
}
