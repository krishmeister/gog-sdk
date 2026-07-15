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

## Exception at startup: "com.gog.sdk C# binding is vX but the bundled native core reports vY"

**Symptom:** `GogSdk.Initialize()` throws with this message; the SDK refuses to start.
**Cause:** the package's C# half and its bundled native `.aar` are from different
versions — someone hand-copied an `.aar`, or the package install is stale/partial. The
two ship together and are verified at boot precisely so a mismatch fails with this
sentence instead of a random crash mid-session.
**Fix:** exactly what the message says — reinstall the `com.gog.sdk` package cleanly.
Never copy `.aar` files between projects or versions by hand.

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

## Something else?

Log output is your first stop — the SDK logs its decisions under the `Unity` logcat
tag (native lines under `GogSdk`). When reporting an issue to GOG, include
`GogSdk.Version` (also printed by the handshake exception, if that's what you're
hitting) and the `RawReason` from any `GogError` you received.
