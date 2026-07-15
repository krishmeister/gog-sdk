# GOG SDK for Unity

Sign players in with their GOG account and use the shared GOG coin wallet — from any
game in the GOG catalogue. One session works across every integrated game (cross-game
SSO), and one coin balance follows the player between them. The SDK handles token
custody, session persistence, and the hub↔game handoff; your game never touches a
refresh token.

**Requires:** Unity **6000.0+** · Android (minSdk **24** / Android 7.0+). Other
platforms compile but every call completes with `UnsupportedPlatform` — safe to leave
in multi-platform projects.

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

## Learn more

- **[Getting started](Documentation~/getting-started.md)** — the full walk-through:
  configuration, both sign-in flows, coins, the launch flow, lifecycle.
- **[Error reference](Documentation~/error-reference.md)** — every `GogErrorCode`, what
  it means for your game, and when to retry.
- **[Troubleshooting](Documentation~/troubleshooting.md)** — symptom → cause → fix for
  the failure modes the SDK deliberately makes loud.
- **Sample:** Package Manager → GOG SDK → *Samples* → **Sign-in & Coins** — a working
  scene you can copy from.
