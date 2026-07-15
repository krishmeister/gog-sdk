# Sample: Sign-in & Coins

**What it shows** — the complete recommended integration in one ~180-line file:
`GogSdk.Initialize()` with graceful not-configured handling, launch-link capture, the
one boot rule (read `CurrentSession`, subscribe to events), "Sign in with GOG", the
shared coin wallet (earn + balance), sign-out, and the `GogResult` error pattern
(switch on `Error.Code`, default-branch, respect `Retryable`) on every call.

**How to run**

1. Import this sample (Package Manager → GOG SDK → *Samples* → **Sign-in & Coins**).
2. Make sure *Project Settings → GOG SDK* has your Game ID set (the SDK's only
   required configuration — Android builds fail without it).
3. Create an empty scene, add an empty GameObject, and attach
   **SignInAndCoinsSample** to it. The sample draws its own UI — nothing to wire.
4. Build & run on an Android device. (In the Editor, calls complete with
   `UnsupportedPlatform` — that's expected; the SDK is Android-only at runtime.)

**What it needs** — a GogSettings asset with a valid Game ID; the GOG hub app on the
device if you want "Sign in with GOG" to succeed (without it you'll see the
`HubNotInstalled` handling instead, which is also worth seeing).
