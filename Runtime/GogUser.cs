namespace Gog.Sdk
{
    /// <summary>The signed-in player's identity. Identity only — never tokens.</summary>
    public sealed class GogUser
    {
        public string Id { get; }
        public string Email { get; }

        public GogUser(string id, string email)
        {
            Id = id;
            Email = email;
        }
    }
}
