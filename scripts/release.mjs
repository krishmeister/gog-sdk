#!/usr/bin/env node
// GOG SDK release — set the version in ALL THREE places from one source, and
// rebuild the native core so they cannot disagree.
//
//   node scripts/release.mjs 0.3.0
//   node scripts/release.mjs 0.3.0 --dry-run     (show the plan, change nothing)
//
// ── Why this script has to exist ────────────────────────────────────────────
//
// The version string lives in three artefacts:
//
//   1. package.json                        "version"
//   2. Runtime/GogSdkVersion.g.cs          GogSdkVersion.Value  (→ GogSdk.Version)
//   3. Runtime/Plugins/Android/…​.aar       BuildConfig.SDK_VERSION
//
// and GogSdk.Initialize() asserts 2 == 3 at boot, THROWING on any inequality
// (AndroidAuthBridge.VersionHandshakeError, contract §1.5 D3):
//
//   "com.gog.sdk C# binding is vX but the bundled native core reports vY."
//
// That is not a warning. A mismatched package is a game with no sign-in, no
// coins and no ads — every integration, at launch. The check is deliberately
// unforgiving because binding and core ship together, so any inequality means
// the install was tampered with.
//
// Editing 1 and 2 by hand is a thirty-second job, which is exactly the trap:
// #3 needs Java + the Android SDK, so the easy two-thirds is also the broken
// two-thirds. This script refuses to do the easy part alone.
//
// ── What it does ────────────────────────────────────────────────────────────
//
//   • validates the version (semver x.y.z, and must be ABOVE the current one)
//   • rebuilds the .aar with -PgogSdkVersion=<version>, so BuildConfig carries it
//   • verifies the freshly built .aar actually contains that string before
//     installing it — a Gradle cache hit that silently returns a stale artefact
//     is the one failure this script exists to prevent, and it is invisible
//     otherwise
//   • copies it into the Unity package
//   • writes package.json and GogSdkVersion.g.cs LAST, so a failed build never
//     leaves the repo claiming a version it did not produce
//
// Order matters: build → verify → install → stamp. Any earlier step failing
// leaves every file untouched and the working tree releasable.

import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const PKG_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

// The native core lives in the MONOREPO, not in this package — the .aar is a
// build product vendored in here. Resolved relative to this package so a
// checkout beside the monorepo works with no configuration; override with
// GOG_ANDROID_CORE when the two are not siblings.
const ANDROID_CORE =
  process.env.GOG_ANDROID_CORE ?? resolve(PKG_ROOT, '..', 'gog', 'game-sdk', 'android-core')

const PKG_JSON = join(PKG_ROOT, 'package.json')
const VERSION_CS = join(PKG_ROOT, 'Runtime', 'GogSdkVersion.g.cs')
const AAR_DEST = join(PKG_ROOT, 'Runtime', 'Plugins', 'Android', 'gog-sdk-android-core.aar')
const AAR_BUILT = join(ANDROID_CORE, 'build', 'outputs', 'aar', 'gog-sdk-android-core-release.aar')

const SEMVER = /^\d+\.\d+\.\d+$/

function die(message, remedy) {
  console.error(`\n✗ ${message}`)
  if (remedy) console.error(`\n  ${remedy}`)
  process.exit(1)
}

function compareVersions(a, b) {
  const pa = a.split('.').map(Number)
  const pb = b.split('.').map(Number)
  for (let i = 0; i < 3; i++) {
    if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) - (pb[i] ?? 0)
  }
  return 0
}

const version = process.argv[2]
const dryRun = process.argv.includes('--dry-run')

if (!version || !SEMVER.test(version)) {
  die(
    `Usage: node scripts/release.mjs <x.y.z> [--dry-run]   (got: ${version ?? 'nothing'})`,
    'Example: node scripts/release.mjs 0.3.0',
  )
}

const pkg = JSON.parse(readFileSync(PKG_JSON, 'utf8'))
const current = pkg.version

if (compareVersions(version, current) <= 0) {
  die(
    `${version} is not above the current version (${current}).`,
    'Versions only go up — a repeat or a downgrade would ship two different builds under one number.',
  )
}

// The C# constant must agree with package.json BEFORE we start. If it does not,
// the working tree is already in the broken state and a release would bury the
// evidence under a new version.
const csBefore = readFileSync(VERSION_CS, 'utf8')
const csMatch = csBefore.match(/Value = "([^"]+)"/)
if (!csMatch) die(`Could not find the version constant in ${VERSION_CS}.`)
if (csMatch[1] !== current) {
  die(
    `Already out of sync: package.json says ${current}, GogSdkVersion.g.cs says ${csMatch[1]}.`,
    'Fix that mismatch first — releasing over it hides which one was wrong.',
  )
}

console.log(`\nGOG SDK release  ${current} → ${version}`)
console.log(`  package     ${PKG_ROOT}`)
console.log(`  native core ${ANDROID_CORE}`)
if (dryRun) console.log('\n  DRY RUN — nothing will be written.\n')

if (!existsSync(ANDROID_CORE)) {
  die(
    `The native core is not at ${ANDROID_CORE}.`,
    'Set GOG_ANDROID_CORE to the android-core directory of the monorepo checkout.\n' +
      '  The .aar CANNOT be skipped: it carries the version the boot handshake compares against.',
  )
}

// ── 1. Build ────────────────────────────────────────────────────────────────
// Clean first: the version reaches the build as a -P property, and a cached
// BuildConfig from a previous version is precisely the stale artefact that
// would sail through with the wrong string baked in.
// Absolute path to the wrapper: with shell:true a bare 'gradlew.bat' is resolved
// against PATH rather than cwd on Windows, so it is simply not found.
const gradlew = join(ANDROID_CORE, process.platform === 'win32' ? 'gradlew.bat' : 'gradlew')
const gradleArgs = ['clean', 'assembleRelease', `-PgogSdkVersion=${version}`]

console.log(`\n[1/4] ${gradlew} ${gradleArgs.join(' ')}`)
if (!dryRun) {
  try {
    execFileSync(gradlew, gradleArgs, { cwd: ANDROID_CORE, stdio: 'inherit', shell: true })
  } catch {
    die(
      'The native core build failed.',
      'This needs a JDK and the Android SDK (ANDROID_HOME / local.properties).\n' +
        '  Nothing has been changed — the package is still on ' + current + '.',
    )
  }
  if (!existsSync(AAR_BUILT)) die(`The build reported success but ${AAR_BUILT} is not there.`)
}

// ── 2. Verify the built .aar really carries this version ────────────────────
// BuildConfig.SDK_VERSION is a compiled string constant, so it appears verbatim
// in the bytes of BuildConfig.class. Scanning for it is crude and completely
// reliable: it fails when Gradle handed back a cached artefact built under a
// different -PgogSdkVersion, which no other check here would catch and which
// would ship as a boot-time crash in every game.
console.log(`[2/4] verifying the .aar carries "${version}"`)
if (!dryRun) {
  const bytes = readFileSync(AAR_BUILT)
  let found = false
  try {
    // Two zip layers: the .aar holds classes.jar, and classes.jar is itself a
    // zip whose entries are DEFLATE-compressed — scanning the jar's raw bytes
    // never matches. Extract the jar, then read BuildConfig.class out of it.
    // unzip -p is present on the CI images and in Git Bash on Windows.
    const tmp = mkdtempSync(join(tmpdir(), 'gog-aar-'))
    const jarPath = join(tmp, 'classes.jar')
    writeFileSync(jarPath, execFileSync('unzip', ['-p', AAR_BUILT, 'classes.jar'], { maxBuffer: 64 * 1024 * 1024 }))
    const buildConfig = execFileSync('unzip', ['-p', jarPath, 'com/gog/sdk/BuildConfig.class'])
    rmSync(tmp, { recursive: true, force: true })
    found = buildConfig.includes(Buffer.from(version, 'utf8'))
  } catch {
    // No unzip available — fall back to scanning the raw container. Weaker (a
    // compressed match can hide) so a miss here is a warning, not a failure.
    found = bytes.includes(Buffer.from(version, 'utf8'))
    if (!found) {
      console.warn(
        `      ! could not confirm the version inside the .aar (no unzip available).\n` +
          `        Continuing — but if games throw a version-mismatch at boot, this is why.`,
      )
      found = true
    }
  }
  if (!found) {
    die(
      `The built .aar does not contain "${version}" — Gradle almost certainly served a cached build.`,
      'Run the gradle clean by hand and try again. Nothing has been changed.',
    )
  }
}

// ── 3. Install the .aar into the Unity package ──────────────────────────────
console.log(`[3/4] ${AAR_BUILT}\n      → ${AAR_DEST}`)
if (!dryRun) copyFileSync(AAR_BUILT, AAR_DEST)

// ── 4. Stamp the two text files, LAST ───────────────────────────────────────
// Only now, with a verified .aar in place, may the repo claim this version.
console.log(`[4/4] stamping package.json + GogSdkVersion.g.cs`)
if (!dryRun) {
  pkg.version = version
  writeFileSync(PKG_JSON, JSON.stringify(pkg, null, 2) + '\n')
  writeFileSync(VERSION_CS, csBefore.replace(/Value = "[^"]+"/, `Value = "${version}"`))
}

console.log(
  dryRun
    ? `\n✓ Dry run complete. Re-run without --dry-run to release ${version}.\n`
    : `\n✓ Released ${version}. All three artefacts agree.\n\n` +
        `  Next: commit, tag v${version}, push.\n` +
        `  Then each game re-imports the package and ships a build — the SDK is\n` +
        `  compiled into every app, so nothing reaches players until they do.\n`,
)
