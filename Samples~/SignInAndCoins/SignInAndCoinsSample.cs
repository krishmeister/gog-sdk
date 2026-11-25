using Gog.Sdk;
using UnityEngine;

namespace Gog.Sdk.Samples
{
    /// <summary>
    /// GOG SDK sample: sign-in + the shared coin wallet, end to end.
    ///
    /// Drop this component on any GameObject in an empty scene and press Play (in the
    /// Editor you'll see UnsupportedPlatform results — expected; on an Android device it
    /// runs for real). It draws its own minimal UI, so there is nothing to wire up.
    ///
    /// This file is intentionally exemplary: every SDK touchpoint here is the pattern we
    /// recommend copying into your game —
    ///   1. Initialize once at boot, then enable launch-link capture.
    ///   2. THE boot rule: read CurrentSession for what already happened; subscribe to
    ///      events for what happens next. (This one rule also covers sign-ins that
    ///      complete after your process was killed mid-flow — no extra code.)
    ///   3. Every async call: one callback, check result.Ok, branch on Error.Code with a
    ///      default case, respect Error.Retryable.
    /// </summary>
    public class SignInAndCoinsSample : MonoBehaviour
    {
        private string _status = "Starting…";
        private long? _balance;               // null until the first successful fetch
        private bool _busy;                   // disables buttons while a call is in flight

        private void Start()
        {
            // 1. One-time boot. Initialize() reads the GogSettings asset (Project
            //    Settings → GOG SDK) and throws ONLY for integration bugs — a missing
            //    asset, a blank Game ID. Guarding it keeps the sample honest about the
            //    not-configured case instead of dying on an exception.
            try
            {
                GogSdk.Initialize();
            }
            catch (System.InvalidOperationException e)
            {
                _status = "Not configured: " + e.Message;
                enabled = true; // keep drawing the status so the message is visible
                return;
            }

            GogDeepLink.Enable(); // capture GOG hub launch links (explicit in 0.1.0)

            // 2a. What already happened: a session restored from a previous run.
            if (GogAuth.CurrentSession.IsSignedIn)
            {
                _status = "Signed in as " + GogAuth.CurrentSession.User.Email + " (restored)";
                RefreshBalance();
            }
            else
            {
                _status = "Signed out";
            }

            // 2b. What happens next: transitions arrive as events — including a sign-in
            //     that finishes headlessly after a process death, and a launch sign-in
            //     from the hub. One subscription covers them all.
            GogAuth.OnSignedIn += user =>
            {
                _status = "Signed in as " + user.Email;
                RefreshBalance();
            };
            GogAuth.OnSignedOut += () =>
            {
                _status = "Signed out";
                _balance = null;
            };
            GogAuth.OnLaunchSignInFailed += err =>
            {
                // A hub-launch sign-in failed; there is no call-site callback for these.
                _status = "Launch sign-in failed: " + err.Code;
            };

            // 3. iOS: a game the player installed from the hub (via the App Store) picks up the
            //    hub's sign-in. Called once, AFTER subscribing, so a success reaches OnSignedIn
            //    above and a failure reaches OnLaunchSignInFailed. Ok(false) means nothing to do
            //    — always the answer off iOS — and the game simply carries on as a guest.
            GogAuth.TrySignInFromInstallHandoff(res =>
            {
                if (res.Ok && !res.Value) Debug.Log("No install handoff — playing as guest.");
            });
        }

        // ── SDK calls (the GogResult pattern, verbatim) ─────────────────────────

        private void SignIn()
        {
            _busy = true;
            _status = "Opening GOG…";
            GogAuth.SignInWithGog(res =>
            {
                _busy = false;
                if (res.Ok) return; // success arrives via OnSignedIn

                switch (res.Error.Code)
                {
                    case GogErrorCode.HubNotInstalled:
                        _status = "GOG hub app not installed — get it from the store.";
                        break;
                    case GogErrorCode.SignInCancelled:
                        _status = "Sign-in cancelled."; // player choice, not an error
                        break;
                    case GogErrorCode.UnsupportedOsVersion:
                        _status = "This flow needs Android 9+. Launch the game from GOG instead.";
                        break;
                    default:
                        // Unknown codes may arrive from future SDK versions — always default-branch.
                        _status = "Sign-in failed: " + res.Error.Code;
                        Debug.LogWarning(res.Error);
                        break;
                }
            });
        }

        private void RefreshBalance()
        {
            GogCoins.GetBalance(res =>
            {
                if (res.Ok) _balance = res.Value.Balance;
                else Debug.LogWarning("Balance: " + res.Error);
            });
        }

        private void EarnTen()
        {
            _busy = true;
            // The SDK generates a retry-safe idempotency key for this call, so a
            // transient network retry can never grant the coins twice.
            GogCoins.Earn(10, "sample-earn", res =>
            {
                _busy = false;
                if (res.Ok)
                {
                    _balance = res.Value.Balance; // post-transaction balance, no refetch needed
                    _status = "Earned 10 coins";
                }
                else if (res.Error.Retryable)
                {
                    _status = "Earn failed (retryable): " + res.Error.Code;
                }
                else
                {
                    _status = "Earn failed: " + res.Error.Code;
                    Debug.LogWarning(res.Error);
                }
            });
        }

        private void SignOut()
        {
            GogAuth.SignOut(); // synchronous local clear; OnSignedOut updates the UI
        }

        // ── Minimal self-contained UI (no scene wiring needed) ──────────────────

        private void OnGUI()
        {
            const int w = 560;
            GUI.skin.button.fontSize = 32;
            GUI.skin.label.fontSize = 28;
            GUI.skin.box.fontSize = 28;

            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, 60, w, Screen.height - 120));
            GUILayout.Box("GOG SDK — Sign-in & Coins sample");
            GUILayout.Space(8);
            GUILayout.Label(_status);
            GUILayout.Label(_balance.HasValue ? "Coins: " + _balance.Value : "Coins: —");
            GUILayout.Space(16);

            GUI.enabled = !_busy && GogSdk.IsInitialized;
            bool signedIn = GogSdk.IsInitialized && GogAuth.CurrentSession.IsSignedIn;

            if (!signedIn && GUILayout.Button("Sign in with Gully Games", GUILayout.Height(72)))
                SignIn();
            if (signedIn && GUILayout.Button("Earn 10 coins", GUILayout.Height(72)))
                EarnTen();
            if (signedIn && GUILayout.Button("Refresh balance", GUILayout.Height(72)))
                RefreshBalance();
            if (signedIn && GUILayout.Button("Sign out", GUILayout.Height(72)))
                SignOut();

            GUI.enabled = true;
            GUILayout.EndArea();
        }
    }
}
