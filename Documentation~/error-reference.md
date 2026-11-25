# GogErrorCode reference (0.1.0)

Every failed call hands you a `GogError` with one of these codes. Two habits keep your
handling future-proof: **always `default`-branch** your switch (the enum grows
additively in future versions), and **trust `Error.Retryable` over your own table** —
the SDK sets it per occurrence, because the same code can be retryable in one context
and not in another.

| Code | What it means for your game | Retry? |
|---|---|---|
| `NotInitialized` | You called the SDK before `GogSdk.Initialize()`. Fix the call order — initialize once at boot. | No — fix integration |
| `InvalidRequest` | The request was malformed or rejected as invalid — an integration bug (bad input, a consumed one-time code reused, …), not a player problem. Retrying sends the same bad request. | No |
| `NetworkError` | Transport failure, timeout, server 5xx, or an unreadable response. The SDK already retried once where that was safe. | **Check `Retryable`.** Usually yes; after a sign-in code exchange it is `false` — the one-time code is likely spent, so re-initiate sign-in instead |
| `Unauthorized` | No signed-in session, or the server rejected it. Prompt sign-in. | No — re-auth first |
| `InternalError` | The server failed (5xx `internal error`), or the SDK met a reason it doesn't recognize (`RawReason` says which). | Yes, with backoff |
| `UnsupportedPlatform` | The call ran somewhere that isn't an Android or iOS device build (e.g. the Editor). Expected in Play Mode; gate on it or ignore. | No |
| `UnsupportedOsVersion` | "Sign in with GOG" needs Android 9 (API 28)+ to verify the hub app. Older devices can still sign in by launching your game *from* the hub. | No — steer to the hub-launch path |
| `RateLimited` | The server asked you to slow down (429). | Yes, with backoff |
| `UnknownGame` | The server doesn't recognize your configured Game ID (wrong id, or the game isn't published yet). Check *Project Settings → GOG SDK* against what GOG issued. | No — fix configuration |
| `InvalidOrExpiredCode` | The one-time sign-in code was expired, already used, or minted for a different game — the server deliberately doesn't say which. Re-initiate sign-in. | No |
| `SignInCancelled` | The player backed out of the GOG sign-in screen. Not an error — just return to your menu. | Player may simply try again |
| `HubNotInstalled` | The GOG hub app isn't on the device. Show your "Get GOG" prompt. (On iOS the SDK has already opened the hub's App Store page — don't open another.) | No — after installing |
| `HubVerificationFailed` | An app claiming to be the GOG hub failed signature verification. Genuinely suspicious — do not retry into it; tell the player to install the real GOG hub from the store. | No |
| `SignInInProgress` | A sign-in is already in flight; the second call was rejected, not queued. Wait for the first to finish. | Yes — wait for the outcome |
| `InsufficientFunds` | Spend exceeds the player's balance. | No — it's a game-design moment, not an error |
| `EarnTooLarge` | The earn exceeded the server-side cap for your game. | No — reduce the amount |
| `IdempotencyKeyReused` | Your supplied idempotency key was replayed **with a different amount/reason**. (Replaying the *same* payload is fine and returns the original transaction.) | No — fix the key handling |
| `AdNotConfigured` | Reserved for the upcoming `GogAds`: the game's ad policy serves AdMob but the build carries no AdMob app id. Not produced by any 0.1.0 call. | No — set `AdmobAppId` and rebuild |

## Reading a GogError

```csharp
if (!res.Ok)
{
    // res.Error.Code      → branch your handling (default-branch unknown values!)
    // res.Error.Retryable → whether retrying THIS failure is sensible
    // res.Error.Message   → developer-facing detail; log it
    // res.Error.RawReason → diagnostics only (bug reports); never branch gameplay on it
    Debug.LogWarning(res.Error);   // ToString() prints "[Code] Message (raw: …)"
}
```
