# ModForge Launcher for Android (modforge-android)

ModForge 安卓启动器：在安卓手机上管理手机版《星露谷物语》的 SMAPI mods，并在本机直接启动带 SMAPI 的游戏。UI 是 [ModForge Studio](https://github.com/Arborsm/ModForge-Studio) 前端（WebView 承载），游戏宿主能力（Assembly Store 提取、IL 重写、定制 Mono runtime、SMAPI 加载链）fork 自 [NRTnarathip/SMAPILoader](https://github.com/NRTnarathip/SMAPILoader)。

> 上游 README（SMAPI Launcher）保留在本文件末尾，功能对照仍适用。

## 与上游 SMAPILoader 的关系

- 本仓库是上游 `1.1.7` tag 的 fork，遵守 GPL-3.0-or-later（见 `LICENSE`、`NOTICE.md`）。
- 已用 ModForge 前端替换上游原生 mod 管理器 UI：
  - `LauncherActivity` 变为全屏 WebView 宿主（`androidx.webkit` WebViewAssetLoader 伺服 `SMAPIGameLoader/Assets/www/` 下的前端构建产物）。
  - `SMAPIGameLoader/Bridge/` 是前端与 C# 之间的 JS 桥（协议见主仓库 `shared/protocol/launcher-commands.json`，生成物在 `Bridge/Generated/`）。
  - `SMAPIGameLoader/Services/` 是从主仓库 Rust `domain/launcher` 移植的 C# 业务层（mods 库扫描/启停、压缩包安装/备份、SMAPI-Android-1.6 release 更新、mod 配置表单、版本门槛）。
  - 前端构建产物需手动从主仓库拷入：主仓库跑 `vp run build`，把产物目录整体覆盖到 `SMAPIGameLoader/Assets/www/`。
- 包名改为 `com.modforge.android`，Mods 目录相应为 `Android/data/com.modforge.android/files/Mods`。

## 构建前置

- .NET 9 SDK（上游 `global.json` pin 9.0.312 + latestFeature）与 android workload（workload 必须装在 .NET 9 SDK band 下，做法见主仓库 `docs/android-launcher-implementation-plan.md`）。
- 上游 `docs/building.md` 所述定制 `libmonosgen-2.0.so` 需按其文档就位。
- 仅支持 arm64 设备、正版游戏 ≥ 1.6.15.3。

## 前端开发服务器（HMR）

想跳过「build → 拷贝 `Assets/www` → 重装 APK」循环调试前端时，可以让 WebView 直接加载 ModForge Studio 仓库的 Vite dev server：

1. 主仓库启动 dev server：`vp run web:dev --port 5175`（HMR WebSocket 默认 5176，即主端口 + 1）。`vp run` 不透传环境变量，必须走 `--port` 参数。
2. 启动器设置页 →「开发服务器」卡片：
   - 模拟器填 `http://10.0.2.2:5175`（`10.0.2.2` 是模拟器看到的宿主 loopback），点「启用」。
   - 真机先在宿主机执行 `adb reverse tcp:5175 tcp:5175` 与 `adb reverse tcp:5176 tcp:5176`，然后填 `http://127.0.0.1:5175`。
3. 保存后活动立即重建并从该地址加载；卡片点「停用」（或不可达恢复页点「Use built-in assets」）即回到内置资源。

实现与边界：

- 覆盖值持久化在 `settings.json` 的 `devServerUrl`（宿主专属调试字段，不进共享协议），bridge 命令 `android:set_dev_server`（`Bridge/ModForgeBridge.cs`），由设置页触发后 `LauncherActivity.Recreate()` 立即生效。
- `Resources/xml/network_security_config.xml` 仅对 `10.0.2.2` / `127.0.0.1` / `localhost` 放行明文 HTTP，其余域保持禁明文——release 构建同样安全。
- WebViewAssetLoader 保持注册：dev server 页面里绝对 `https://appassets.androidplatform.net/local-file/...`、`/plugins/...` 子资源仍从 APK 资产与沙箱伺服。
- dev server 主框架加载失败时，`AssetLoaderWebViewClient` 会加载内联恢复页（重试 / 清除并回内置资源），不留死胡同。

---

# SMAPI Launcher (upstream)

![image](https://github.com/user-attachments/assets/09a5f3fa-0b99-4aae-8f47-2de9009d5209)

# 🌟 Key Features
- Mod Manager
- Share Log by 1 click
- Update SMAPI or Launcher by 1 click, without Uninstall any SMAPI or Launcher
- No need install new game apk
- Need to download game from Play Store only, not support game apks modify or pirate game
- Support device 64bit only

# 🍕 Upcoming Feature
- Save Import & Export

# Discord Server SMAPI Thailand
https://discord.com/invite/ETtycvcJjr

# How To Install Wiki
https://github.com/NRTnarathip/SMAPILoader/wiki/How-To-Install-SMAPI

# Special Thanks
- Eky-Team for creating guides and resources to patch .NET 8 to .NET 9
- Pathoschild for support, guidance, and the community
