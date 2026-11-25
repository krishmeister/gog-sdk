# Troubleshooting (0.1.0)

The SDK's failure modes are designed to be **loud and early** — a failed build or a
clear log line instead of a silent broken feature in a shipped APK. Here is each one:
symptom → cause → fix.

## Build fails: "set a valid Game ID in Project Settings → GOG SDK before building"

**Symptom:** the Android build stops immediately with this message.
**Cause:** the GogSettings asset is missing, or its Game ID is blank / not a UUID. The
build validator fails on purpose — building on would ship a broken sign-in App-Link.
**Fix:** *Project Settings → GOG SDK* → create the asset if needed and paste the exact
Game ID (a UUID) GOG issued for your game.

## Build fails: "AdmobAppId '…' is not a valid AdMob APP id"

**Symptom:** the Android build stops naming your AdmobAppId value.
**Cause:** the value isn't the AdMob **app id** shape
(`ca-app-pub-<16 digits>~<10 digits>`, with a **`~`**). The most common cause: an ad
**unit** id (contains **`/`**) was pasted where the app id belongs. AdMob crashes the
app at launch on a malformed app id, so the SDK fails your build instead.
**Fix:** paste the app id from the AdMob console (Apps → App settings), or clear the
field entirely — blank is always safe and simply injects no AdMob metadata.

## AdMob ads never appear: "AdMob interstitial not shown (gma_missing)"

**Symptom:** GOG has set your game up for AdMob, `ShowInterstitial` always resolves `NoAd`,
and the log (tag `Unity`) shows this warning.
**Cause:** Google's `play-services-ads` library is not in your APK. The SDK asks for it
through `GogSdkDependencies.xml`, but only the External Dependency Manager for Unity
(EDM4U) reads that file.
**Fix:** install EDM4U, run *Assets → External Dependency Manager → Android Resolver →
Resolve*, set the AdMob app id (next entry), and rebuild.

## AdMob ads never appear: "AdMob interstitial not shown (app_id_missing)"

**Symptom:** as above, but the warning names `app_id_missing`.
**Cause:** *Project Settings → GOG SDK → AdMob app id* is blank, so no app id is in your
manifest.
**Fix:** paste the `ca-app-pub-…~…` app id from the same AdMob account as your game's ad
units, and rebuild.

## AdMob ads rarely appear: "AdMob interstitial not shown (load_failed:3 …)"

**Symptom:** a plain log line (not a warning) with `load_failed:3` or `load_timeout`.
**Cause:** not an SDK fault — Google had no ad for this request (code 3 is "no fill"), or
the fill took too long. New AdMob apps, apps not yet linked to a Play Store listing, and
apps without an `app-ads.txt` on the developer website get little or no fill.
**Fix:** nothing in your build. Link the app to its store listing in the AdMob console and
check it for "limited ad serving" notices.

## Exception at startup: "com.gog.sdk C# binding is vX but the bundled native core reports vY"

**Symptom:** `GogSdk.Initialize()` throws with this message; the SDK refuses to start.
**Cause:** the package's C# half and its bundled native `.aar` are from different
versions — someone hand-copied an `.aar` (or, on iOS, a `GOG.xcframework`), or the package install is stale/partial. The
two ship together and are verified at boot precisely so a mismatch fails with this
sentence instead of a random crash mid-session.
**Fix:** exactly what the message says — reinstall the `com.gog.sdk` package cleanly.
Never copy `.aar` files between projects or versions by hand.

## iOS build fails: "GOG.xcframework is missing from the com.gog.sdk package"

**Symptom:** an iOS build stops immediately with this message.
**Cause:** the package has no `Runtime/Plugins/iOS/GOG.xcframework` — the native iOS core.
Without it the Xcode project could not link (or would ship an app with no sign-in), so the
SDK fails the build instead.
**Fix:** install a `com.gog.sdk` release that includes the framework, or have it built on a
Mac as described in `Runtime/Plugins/iOS/README.md`.

## iOS build fails: "GOG.xcframework requires iOS 17.4 or newer"

**Symptom:** an iOS build stops naming your *Target minimum iOS Version*.
**Cause:** the native core is built for iOS 17.4+. A lower target would install on devices
where the app crashes at launch.
**Fix:** *Player Settings → Other Settings → Target minimum iOS Version* → `17.4` or higher.

## iOS: "Sign in with GOG" never completes, or every launch link is ignored

**Symptom:** the hub opens and the player approves, but back in the game the call completes
with `SignInCancelled`; or a hub launch opens the game without signing in.
**Cause 1:** `GogDeepLink.Enable()` was never called. On iOS both flows return through the
game's Universal Link, which only reaches the SDK through `GogDeepLink` (the SDK logs a
warning when `SignInWithGog` is called without it).
**Cause 2:** the Universal Link is not routed to your app: the App ID lacks the *Associated
Domains* capability, or GOG has not yet listed your Team ID + bundle id for your game (iOS
caches the association file, sometimes for days). The Xcode console shows the SDK's warning
with the received URL when a link arrives but is not this game's.
**Fix:** call `Enable()` right after `GogSdk.Initialize()`; enable Associated Domains for the
App ID; confirm the entitlements contain `applinks:link.godofgaming.online` (the SDK adds it
at build time).

## Every call returns `NotInitialized`

**Symptom:** callbacks fire immediately with `Error.Code == NotInitialized`.
**Cause:** SDK calls ran before `GogSdk.Initialize()` — usually a script-execution-order
issue (some other `Start()`/`Awake()` runs before your bootstrap).
**Fix:** call `GogSdk.Initialize()` once, early (an `Awake()` on a boot object works),
before anything else touches the SDK. Calls never throw for this — they report it — so
check `GogSdk.IsInitialized` if you're unsure where you are in the boot order.

## A GOG launch opens the game, but no sign-in happens

**Symptom:** tapping the game in the GOG hub opens your game, but the player isn't
signed in — and logcat (filter tag `Unity`) shows:
`[GOG SDK] Ignoring a deep link that is not a valid GOG launch link: "<url>" …`
**Cause 1:** `GogDeepLink.Enable()` was never called — in 0.1.0 launch capture is
explicit (call it right after `GogSdk.Initialize()`; it becomes automatic in 1.0.0).
**Cause 2:** the link's host/path doesn't match what the SDK expects — the warning
prints both the received URL and the expected shape. GOG is migrating launch links to
their long-term host in 1.0.0; if you hit this during 0.1.0 integration testing, align
`GogDeepLink.Host` / `PathPrefix` with the URL shown in the warning (these statics are
temporary knobs and go away when the migration completes).
**Fix:** add the `Enable()` call, or match the host per the warning. The warning line
always tells you which of the two you're in.

## Sign-in doesn't work after the game launches from the hub — and there's no error

**Symptom:** the player taps your game in the GOG hub, the game launches, but no sign-in
happens — and unlike the case above there is **no warning in your logs and no error on
either side**. It silently does nothing.
**Cause:** your game's App Link is not *verifying*, so Android never hands the signed
sign-in link to your game. App Link verification requires the signing certificate
registered for your game to match the certificate the **shipped** APK is actually signed
with — and the number-one way they diverge is Google Play App Signing. When you upload to
Play, Google **re-signs** your app with the Play App Signing key, so the certificate the
installed app carries is the *Play App Signing* key's — **not** your upload key's. If the
fingerprint registered for your game was the upload key's, the App Link silently never
verifies, and sign-in fails with no error on either side.
**Fix:** register the **Play App Signing** certificate's SHA-256 for your game — copy it
from *Play Console → your app → App integrity → App signing* (the *app signing key
certificate*, not the upload key certificate). For a sideloaded or debug build, which
Play never re-signs, register that build's own signing fingerprint instead. Once the
registered fingerprint matches the certificate the shipped APK is signed with, the App
Link verifies and the launch hands off cleanly.

## Sign-in is declined: the hub says "This game isn't registered with GOG"

**Symptom:** the player taps "Sign in with GOG", the GOG hub opens, and instead of signing
in it shows **"This game isn't registered with GOG"** and declines — even though your game
built, installed, and launched normally.
**Cause:** the **Game ID** in *Project Settings → GOG SDK* is a valid UUID but does not
match a game registered in the GOG dashboard, so the hub can't find it and refuses. Because
the ID is well-formed, the build validator passes and the handoff runs — the mismatch only
surfaces here, at sign-in. This is **not** the certificate case above, which fails
*silently* with no message; here the hub names the problem on screen. (The same condition
reaches your code as the `UnknownGame` error — see the [error reference](error-reference.md).)
**Fix:** open *Project Settings → GOG SDK* and confirm the Game ID **exactly** matches your
game's ID in the GOG dashboard — copy it from the dashboard rather than retyping it. Then
**rebuild**: the Game ID is baked into the build (it also drives the App-Link manifest
injection), so a settings change takes effect only in a new build.

## The app appears signed in right after a "clean" reinstall

**Symptom:** during QA you `adb shell pm clear` the app (or uninstall) and then
`adb install -r`, expecting a signed-out slate — but the app boots already signed in.
**Cause:** this is a test-environment gotcha, **not** an SDK bug. Google's
restore-at-install can asynchronously replant the app's data (including the encrypted
session) in the window right after the clear, so `pm clear` + reinstall is **not** a
reliable sign-out on consumer Android. **Fix:** to get a genuine signed-out state, use the
in-app sign-out (`GogAuth.SignOut()`), or `pm clear` **without** a reinstall, and confirm
the real state from `GogAuth.CurrentSession` rather than assuming the clear took.

## Something else?

Log output is your first stop — the SDK logs its decisions under the `Unity` logcat
tag (native lines under `GogSdk`). When reporting an issue to GOG, include
`GogSdk.Version` (also printed by the handshake exception, if that's what you're
hitting) and the `RawReason` from any `GogError` you received.
