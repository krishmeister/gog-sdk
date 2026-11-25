# Runtime/Plugins/iOS — the native iOS core

This folder holds **`GOG.xcframework`**, the iOS counterpart of
`../Android/gog-sdk-android-core.aar`. It is **not** checked in yet; it is a build product.

## Where it comes from

Built on a Mac (Xcode required) from the `Gog-ios-sdk` repository:

```bash
scripts/build-xcframework.sh --package-json "/path/to/com.gog.sdk/package.json"
```

Passing this package's `package.json` stamps the framework with the same version as
`package.json` and `Runtime/GogSdkVersion.g.cs`. `GogSdk.Initialize()` compares the two at
boot (`gog_bridge_sdk_version`) and **throws on any difference**, exactly like the Android
`.aar` handshake — so never hand-copy a framework from another version.

Copy the result, `dist/GOG.xcframework`, to `Runtime/Plugins/iOS/GOG.xcframework`.

## What uses it

- `Runtime/Internal/IosAuthBridge.cs` calls its `gog_bridge_*` C functions through
  `[DllImport("__Internal")]`.
- `Editor/GogIosPostProcess.cs` reads it **straight from this folder** at build time, copies it
  into the exported Xcode project, links it into `UnityFramework`, and Embeds & Signs it in the
  `Unity-iPhone` app target. It does not rely on Unity's plugin importer, so the framework works
  whether or not Unity imports it. If Unity does import and copy it, the post-process reuses that
  copy instead of adding a second. (Recommended: when you add the framework, let Unity generate
  its `.meta` in a writable checkout, untick every platform in its Inspector so only the
  post-process wires it, and commit that `.meta` with it.)
- An iOS build **fails in Unity, with a message naming this file**, when the framework is
  missing — rather than producing an Xcode project that cannot link or an app with no sign-in.

The framework requires **iOS 17.4+**; iOS builds with a lower *Target minimum iOS Version* fail
with a message saying so.
