# GOG SDK for Unity

Sign players in with their GOG account and use the shared GOG coin wallet — from any
game in the GOG catalogue. One session works across every integrated game (cross-game
SSO), and one coin balance follows the player between them. The SDK handles token
custody, session persistence, and the hub↔game handoff; your game never touches a
refresh token.

**Requires:** Unity **6000.0+** · Android (minSdk **24** / Android 7.0+) · iOS
(**17.4+**, with `Runtime/Plugins/iOS/GOG.xcframework` in the package — see
[iOS](#ios)). Other platforms compile but every call completes with
`UnsupportedPlatform` — safe to leave in multi-platform projects.

## Install in three steps

1. **Add the package.** Package Manager → *Add package from tarball…* (or from disk),
   using the `com.gog.sdk` package GOG provides. Its one dependency
   (`com.unity.nuget.newtonsoft-json`) resolves automatically.
2. **Set your Game ID.** *Project Settings → GOG SDK* → create the settings asset and
   paste the Game ID (a UUID) GOG issued for your game. This asset is the SDK's **only**
   configuration source — there is nothing to pass in code. A missing or invalid Game ID
   **fails the Android build on purpose** (`GogBuildValidator`), because it would
   otherwise ship a broken sign-in link.
3. **Build for Android.** The SDK injects everything it needs (the GOG App-Link intent
   filter, and — only if you set an AdMob app id — that too) into the generated manifest
   at build time. No manifest editing, no Gradle editing.

## Minimal integration (sign in + balance)

```csharp
using Gog.Sdk;
using UnityEngine;

public class GogBootstrap : MonoBehaviour
{
    void Start()
    {
        GogSdk.Initialize();     // reads the GogSettings asset; throws only on integration bugs
        GogDeepLink.Enable();    // capture hub launch links (0.1.0: explicit; becomes automatic in 1.0.0)

        // THE one boot rule: read CurrentSession for what already happened,
        // subscribe to events for what happens next.
        if (GogAuth.CurrentSession.IsSignedIn)
            Debug.Log("Welcome back, " + GogAuth.CurrentSession.User.Email);

        GogAuth.OnSignedIn += user =>
        {
            Debug.Log("Signed in: " + user.Email);
            GogCoins.GetBalance(res =>
            {
                if (res.Ok) Debug.Log("Coins: " + res.Value.Balance);
                else Debug.LogWarning("Balance failed: " + res.Error.Code);
            });
        };
        GogAuth.OnLaunchSignInFailed += err =>
            Debug.LogWarning("Launch sign-in failed: " + err.Code);

        // iOS: pick up the hub's sign-in after an App Store install. Once, after subscribing.
        // Ok(false) = nothing to do (always, off iOS) → carry on as a guest.
        GogAuth.TrySignInFromInstallHandoff(res => { });
    }

    public void OnSignInButton()
    {
        GogAuth.SignInWithGog(res =>
        {
            if (!res.Ok) Debug.LogWarning("Sign-in: " + res.Error.Code + " — " + res.Error.Message);
            // success arrives via OnSignedIn above
        });
    }
}
```

Every asynchronous call takes exactly one callback and always calls it exactly once, on
the main thread, with a `GogResult<T>`: check `Ok`, then use `Value` or `Error`.

## iOS

The same C# API runs on iPhone over the native Swift core, `GOG.xcframework`
(`Runtime/Plugins/iOS/`, built on a Mac — see the README there). Building for iOS:

- **Fails in Unity, on purpose,** when the Game ID is missing, when the package carries no
  `GOG.xcframework`, or when *Target minimum iOS Version* is below **17.4** — each would
  otherwise ship an app that cannot sign in or cannot launch.
- **Wires the Xcode project for you** (`Editor/GogIosPostProcess.cs`): links the framework
  into `UnityFramework`, Embeds & Signs it in the app, and merges into the app's
  entitlements `keychain-access-groups` = `$(AppIdentifierPrefix)<your bundle id>` **then**
  `$(AppIdentifierPrefix)online.godofgaming.shared` (your own group must stay first — it is
  where your players' sessions are stored), plus `applinks:link.godofgaming.online`. Your
  App ID needs the *Associated Domains* capability enabled.

At runtime:

- Launch links and the "Sign in with GOG" return both arrive as Universal Links through
  `GogDeepLink` — so `GogDeepLink.Enable()` is **required** on iOS. "Sign in with GOG" opens
  the GOG hub app; without the hub, the SDK opens its App Store page and the call completes
  with `HubNotInstalled`. Coming back without finishing completes with `SignInCancelled`.
- `GogAuth.TrySignInFromInstallHandoff(callback)` signs in a player who installed the game
  from the hub via the App Store: the hub leaves a one-time code in a Keychain group shared by
  GOG's apps, and this call exchanges it. Call it once after `Initialize()` and your event
  subscriptions; `Ok(true)` = signed in (`OnSignedIn` fires too), `Ok(false)` = nothing to do
  (always on Android and in the Editor), an error = the exchange failed (also raised on
  `OnLaunchSignInFailed`). See [Getting started §7a](Documentation~/getting-started.md).
- Coins and ads work exactly as on Android. AdMob directives resolve as `NoAd` on iOS
  (house ads, image and video, are served normally).

## Learn more

- **[Getting started](Documentation~/getting-started.md)** — the full walk-through:
  configuration, both sign-in flows, coins, the launch flow, lifecycle.
- **[Error reference](Documentation~/error-reference.md)** — every `GogErrorCode`, what
  it means for your game, and when to retry.
- **[Troubleshooting](Documentation~/troubleshooting.md)** — symptom → cause → fix for
  the failure modes the SDK deliberately makes loud.
- **Sample:** Package Manager → GOG SDK → *Samples* → **Sign-in & Coins** — a working
  scene you can copy from.
