# NOTICE

This repository, `Arborsm/modforge-android`, is a fork of
[NRTnarathip/SMAPILoader](https://github.com/NRTnarathip/SMAPILoader)
(upstream tag `1.1.7`), the .NET Android game host that extracts the managed
assemblies of the user's legally installed Stardew Valley APK, applies
Mono.Cecil IL rewrites and loads SMAPI in-process.

## Copyright

- The upstream SMAPILoader code and its bundled patched Mono runtime remain
  Copyright (c) NRTnarathip and SMAPILoader contributors, licensed under the
  GNU General Public License v3.0 (see `LICENSE`).
- ModForge shell changes (WebView launcher host, JS bridge, C# launcher
  services under `SMAPIGameLoader/Bridge` and `SMAPIGameLoader/Services`)
  are Copyright (c) 2026 the ModForge Studio authors. The combined work is
  distributed under GPL-3.0-or-later, as required by the upstream license.

## Runtime third-party components

- SMAPI for Android is **not bundled**. The launcher downloads the release zip
  of [NRTnarathip/SMAPI-Android-1.6](https://github.com/NRTnarathip/SMAPI-Android-1.6)
  (fork of Pathoschild/SMAPI, LGPL-3.0) at runtime, extracts it, and loads it.
  Its source and license are available at the links above.
- The launcher does not distribute any Stardew Valley game code or assets. It
  only reads the game the user has purchased and installed from Google Play or
  the Galaxy Store, via public Android APIs, on-device.
