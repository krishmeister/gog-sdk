# Getting started with the GOG SDK

This guide assumes nothing beyond a working Unity 6000.0+ Android project. It documents
the SDK **as shipped in 0.1.0** — where something changes in 1.0.0, that is called out
explicitly so you never copy an example that doesn't compile.

## 1. Install

Package Manager → *Add package from tarball…* (or from disk) with the `com.gog.sdk`
package GOG provides. The package brings its own native Android core (a bundled `.aar`
plus its Gradle dependencies) and one Unity dependency, Newtonsoft JSON, which UPM
resolves automatically. You edit no manifest and no Gradle template.

Android only: minSdk 24 (Android 7.0). On any other platform (including the Editor's
Play Mode) the SDK compiles and initializes, and every call completes with an
`UnsupportedPlatform` error result — so you can leave the integration code in
multi-platform builds unguarded.

## 2. Configure — one asset, no code

*Project Settings → GOG SDK* → **Create GOG SDK Settings**, then set:

| Field | What it is |
|---|---|
| **Game ID** | The UUID GOG issued for your game. Required — Android builds fail until it is a valid UUID. |
| **API Base URL** | Leave the default. Only changes for a GOG-provided staging setup. |
| **Auto sign-in from launch** | Reserved: in 0.1.0 this toggle is not yet consumed — launch capture is enabled by the explicit `GogDeepLink.Enable()` call (step 3). In 1.0.0 it becomes automatic and this toggle controls it. |
| **AdMob app id** | Leave blank unless GOG has set your game up for AdMob (see §8). |

This asset is the SDK's **single** source of configuration. The same values drive the
build-time Android manifest injection and the runtime, so they cannot disagree. There is
deliberately no `Initialize(config)` overload — nothing to pass, nothing to drift.

## 3. Initialize once, at boot

```csharp
using Gog.Sdk;
using UnityEngine;

public class GameBoot : MonoBehaviour
{
    void Start()
    {
        GogSdk.Initialize();
        GogDeepLink.Enable();   // start catching GOG launch links (see §7)
    }
}
```

`GogSdk.Initialize()`:

- reads the GogSettings asset and wires up the native core;
- **restores any persisted session** — a player who signed in last week is already
  signed in when `Initialize()` returns (read `GogAuth.CurrentSession`);
- **throws only for integration bugs** — a missing/invalid settings asset, a call off
  the main thread, or a package whose C# and native halves disagree (see
  [troubleshooting](troubleshooting.md)). Runtime problems (network, auth state) never
  throw; they arrive as error *results* on the calls you make.

A second `Initialize()` call is a logged no-op. `GogSdk.IsInitialized` tells you where
you are. Calls made *before* `Initialize()` don't throw either — they complete with a
`NotInitialized` error result.

## 4. The async model — one pattern for every call

Every asynchronous SDK method takes **exactly one required callback**, always the last
parameter, and always invokes it **exactly once, on the Unity main thread** (UI is
always safe to touch). The payload is a `GogResult<T>`:

```csharp
GogCoins.Spend(25, "hat", res =>
{
    if (res.Ok)
    {
        // res.Value is a GogCoinTransaction here
        ApplyPurchase(res.Value.Balance);
        return;
    }
    switch (res.Error.Code)
    {
        case GogErrorCode.InsufficientFunds:
            ShowGetMoreCoins();
            break;
        case GogErrorCode.Unauthorized:
            PromptSignIn();
            break;
        case GogErrorCode.NetworkError:
            if (res.Error.Retryable) ScheduleRetry();
            else ShowOffline();
            break;
        default:
            // Always default-branch: the enum grows additively in future versions.
            Debug.LogWarning("Spend failed: " + res.Error);
            break;
    }
});
```

- `Ok` — exactly one of `Value` / `Error` is meaningful.
- `Error.Code` — the [error reference](error-reference.md) is the full list.
- `Error.Retryable` — set **per occurrence**, not per code: the same `NetworkError` is
  retryable on a balance fetch but not after a sign-in code was likely consumed. Trust
  the flag over your own table.
- `Error.Message` is developer-facing (log it); `Error.RawReason` is diagnostics only.

There is no second async style: no exceptions for runtime failures, no optional error
callback to forget, no event that stands in for a return value.

## 5. Signing players in

Two flows, both ending in the same signed-in session:

**"Sign in with GOG" (the reverse handoff)** — the player taps a button in *your game*;
the GOG hub app opens, the player approves, and control returns to your game already
signed in. Use this for your sign-in button:

```csharp
GogAuth.SignInWithGog(res =>
{
    if (!res.Ok)
    {
        switch (res.Error.Code)
        {
            case GogErrorCode.HubNotInstalled:  ShowGetGogHubPrompt(); break;
            case GogErrorCode.SignInCancelled:  break; // player backed out — not an error
            default: Debug.LogWarning("Sign-in failed: " + res.Error); break;
        }
    }
    // on success OnSignedIn fires (after this callback) — see §6
});
```

If your game's process is killed while the player is in the hub (common on low-memory
phones), the sign-in **still completes** on the way back — see §6 for the one rule that
makes this free for you.

**Sign-in with a code (`SignInWithCode`)** — the escape hatch for games that receive a
GOG one-time code through their own plumbing (for example a custom deep-link stack):

```csharp
GogAuth.SignInWithCode(code, res =>
{
    if (!res.Ok) Debug.LogWarning("Code exchange failed: " + res.Error.Code);
});
```

Most games never call this directly — the launch flow (§7) does it for you.

**Signing out** is synchronous (a local credential clear, no network):

```csharp
GogAuth.SignOut();   // clears the stored session; OnSignedOut fires
```

**Access tokens** — if your own backend needs to call GOG on the player's behalf:

```csharp
GogAuth.GetAccessToken(res =>
{
    if (res.Ok) SendToMyBackend(res.Value);   // a short-lived bearer token
});
```

The SDK refreshes tokens on demand; you never see (and never store) a refresh token.

## 6. Session state — the one boot rule

> **On boot, read `CurrentSession`; subscribe to events for what happens next.**

```csharp
// after GogSdk.Initialize():
if (GogAuth.CurrentSession.IsSignedIn)
    ShowWelcome(GogAuth.CurrentSession.User.Email);   // restored — no event replays this

GogAuth.OnSignedIn          += user => ShowWelcome(user.Email);
GogAuth.OnSignedOut         += ()   => ShowSignedOut();
GogAuth.OnLaunchSignInFailed += err => ShowLaunchProblem(err);
```

Why both halves matter:

- `CurrentSession` is **authoritative state**, restored from secure storage during
  `Initialize()`. Events are **news** — they fire on transitions and never replay
  what happened before this process started.
- The reverse handoff **survives process death**: if Android killed your game while the
  player was in the hub, the SDK completes the exchange headlessly when your game comes
  back, and the result reaches you as `OnSignedIn` (or `OnLaunchSignInFailed`) — there
  is no call-site callback anymore, because that code died with the old process.

Follow the boot rule and both cases — restored session and headless completion — are
handled with no extra code.

`OnSignedOut` also fires unsolicited if the session dies server-side (an expired
refresh). In 0.1.0 it carries no argument; 1.0.0 adds a reason parameter.

## 7. The launch flow — players arriving from the GOG hub

When a player taps your game inside the GOG hub, Android opens your game through a
verified App Link that carries a one-time sign-in code. With `GogDeepLink.Enable()`
called (step 3), the SDK captures that link — cold start or already-running — extracts
the code, exchanges it, and raises `OnSignedIn`. The player experience: tap the game in
GOG, land in your game already signed in, zero taps.

You integrate nothing beyond `Enable()` + the §6 boot rule. The App-Link intent filter
is injected into your Android manifest automatically at build time from your Game ID.
(In 1.0.0, `Enable()` itself becomes automatic and the *Auto sign-in from launch*
toggle controls it.)

If a launch link arrives that the SDK does not recognize, it logs a loud warning with
the received URL and what it expected — see [troubleshooting](troubleshooting.md).

## 8. Coins — the shared wallet

One balance per player, shared across all GOG games. Amounts are positive whole
numbers. All calls require a signed-in session (`Unauthorized` otherwise).

```csharp
GogCoins.GetBalance(res => { if (res.Ok) SetCoinLabel(res.Value.Balance); });

GogCoins.Earn(10, "level-complete", res =>
{
    if (res.Ok) SetCoinLabel(res.Value.Balance);   // res.Value: GogCoinTransaction
});

GogCoins.Spend(25, "hat", res =>
{
    if (res.Ok) { Unlock("hat"); SetCoinLabel(res.Value.Balance); }
    else if (res.Error.Code == GogErrorCode.InsufficientFunds) ShowGetMoreCoins();
});
```

`GogCoinTransaction` gives you the post-transaction `Balance`, a `TransactionId`, and
`Applied`. **Idempotency is built in**: the SDK generates a retry-safe key per call and
retries transient failures with the *same* key, so a retried earn/spend can never apply
twice. If your game has its own retry loop, use the overloads that take your own
`idempotencyKey` — replaying the same key returns the original transaction with
`Applied == false` instead of double-charging.

## 9. Lifecycle

`GogSdk.Shutdown()` returns the SDK to its pre-init state: event subscriptions are
cleared and the native bridge is dropped. It does **not** sign the player out — the
persisted session survives and the next `Initialize()` restores it. You rarely need it
in a game; it exists for clean teardown and is also what keeps *Enter Play Mode without
Domain Reload* leak-free in the Editor.

## 10. AdMob (forward-looking — read before you wait on it)

**`GogAds` does not ship in 0.1.0.** Interstitials (`GogAds.ShowInterstitial`) arrive
in a future version as an additive update — no rework on your side. What ships *now* is
the build-time plumbing, and it ships now for one reason: the AdMob **app id** lives in
your APK's manifest, so it must be in your build *before* release, or adding ads later
would force a re-release.

If (and only if) GOG has set your game up for AdMob:

- Enter the **AdMob app id** in *Project Settings → GOG SDK*. App ids look like
  `ca-app-pub-1234567890123456~1234567890` — note the **`~`**. An ad **unit** id
  (with a **`/`**) is a different identifier; pasting one here is the classic mistake,
  and the build fails with a clear message if you do (better a failed build than an
  app that crashes on launch, which is what a malformed app id causes).
- The id must belong to the **same AdMob account** as the ad units GOG configures for
  your game server-side. Mismatched accounts serve nothing.
- Blank is always safe: no AdMob metadata enters your manifest at all.

Nothing else to do until `GogAds` ships. Design rule to keep in mind now: game flow
must never depend on an ad rendering — "no ad" will be a normal, frequent outcome.
