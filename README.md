# BAwiki Framework

**A local-first Blue Archive archive platform with a full LDTE audio analysis toolkit.**

English | [中文简介](#中文简介)

---

## Overview

BAwiki Framework is the complete source code behind **BAwiki Web (基沃托斯古书馆 · 本地离线版)** — a Windows-first, fully offline Blue Archive reference site. It ships as a single-file .NET 8 server plus a native launcher, with a framework-free HTML/CSS/JS frontend.

The repository also contains the **LDTE** audio subsystem: a compact binary analysis format, a desktop viewer, an encrypted clip format, an offline decryptor, and a Cloudflare Worker for remote key issuing and decryption.

Nothing here depends on a cloud service to run. The site scans a local data directory, builds an index, and serves everything from `127.0.0.1`.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/` | .NET 8 server (`BAwikiWeb.cs`), HTTP routing, indexing, media streaming |
| `src/Server/` | Feature modules: AI assistant, LDTE, online update, import, plugins, profiles |
| `src/LdteViewer/` | WinForms desktop viewer for `.ldte` archives (`LDTE-Viewer.exe`) |
| `src/BAwikiDecrypt/` | Offline `.ldtc` decryptor with waveform preview and CLI mode |
| `src/BAudioBridge/` | Local audio engine bridge (FFmpeg decode, multichannel output) |
| `src/BAudioExclusive/` | WASAPI exclusive-mode audio engine |
| `src/BAwikiLauncher/` | Native C++ launcher (UAC, console piping, diagnostics) |
| `web/` | Framework-free frontend: gallery, music player, PV, admin console |
| `cloudflare-worker/` | Worker for `CFK1` token issuing, verification, revocation, decryption |
| `tools/` | Standalone browser tool for LDTE/LDTC inspection |

## Feature highlights

- **Offline reference site** — students, gallery, memory lobby, official PV, Bilibili videos, music player.
- **Incremental resource updater** — audits six resource categories per character and fills only what is missing.
- **LDTE audio format** — 12-channel, 16 ms/frame training data with peak segments and derived profiles.
- **Encrypted clips (LDTC1)** — double-layer AES-256-GCM; local master key or cloud-issued `CFK1` tokens.
- **AI ops console** — tool-calling assistant with per-account session history and an audit view of operations conversations.
- **Admin panel** — accounts, permissions, quotas, cache, plugins, GPU, LDTE keys, cloud keys.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK (server, viewer, decryptor, audio engines)
- MSVC `cl.exe` (only for rebuilding the native launcher)
- Optional: FFmpeg on `PATH` for video thumbnails and audio import
- Optional: Cloudflare account + KV for the cloud key worker

## Build

```powershell
# Server (single-file publish)
dotnet publish src/BAwikiWeb.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o out

# Desktop viewer
dotnet publish src/LdteViewer/LdteViewer.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true -o out

# Native launcher
cd src/BAwikiLauncher
build.bat

# Cloudflare Worker
cd cloudflare-worker
npx wrangler deploy
```

## Run

```powershell
BAwikiWeb.exe
```

The server scans `kivo_wiki` (or a data directory you specify), builds an index, prints its console commands, then opens the site.

Console commands: `AR` full resource update · `RI` rebuild index · `ST` status · `HELP` list · `AI ...` assistant · `Q` quit.

If the process has no usable console, write commands to `console_cmd.txt` (one per line); output is appended to `console_out.txt`.

## Configuration and secrets

No credentials are committed to this repository. Real deployments use:

- `_本地维基/index/ai.json` — AI API key, model, thinking level
- `_keys/master.key` — 32-byte LDTE master key
- `_keys/cloud.json` — worker base URL and admin secret
- `_本地维基/index/accounts.json` — site accounts

The source contains placeholders only (`_aiKey = ""`, `AuthPsk = "CHANGE_ME_AUTH_PSK"`). Replace them at deploy time, or better, keep them in local config files outside version control.

## Resource library (Baidu Netdisk)

> **Please read this before downloading anything.**
>
> The framework source is small. The **resource library is large (tens of GB)** and is distributed separately through Baidu Netdisk.
>
> **If you only want to study or reuse the framework, do NOT download the resource library.** The code runs with your own data directory, and the architecture is fully readable without any game assets.
>
> Only download the pack if you actually intend to run a complete offline archive locally. See [`docs/RESOURCES.md`](docs/RESOURCES.md) for the link, layout, and integrity notes.

## 中文简介

**BAwiki Framework 是「基沃托斯古书馆 · 本地离线版」的完整源码框架。**

它是一套完全本地运行的 Blue Archive 资料站：单文件 .NET 8 服务端 + 原生启动器 + 零框架前端，离线扫描本地数据目录并建立索引。仓库同时包含 LDTE 音频子系统：训练数据格式、桌面查看器、LDTC1 加密片段、离线解密工具与 Cloudflare 密钥 Worker。

**主要内容**

- `src/`：服务端与各功能模块（AI 助手、LDTE、在线更新、导入、插件、用户配置）
- `src/LdteViewer/`、`src/BAwikiDecrypt/`：桌面查看器与离线解密器
- `src/BAudioBridge/`、`src/BAudioExclusive/`：本地音频引擎
- `web/`：图鉴、画廊、留声机、PV、管理后台前端
- `cloudflare-worker/`：云端密钥签发 / 验签 / 吊销 / 解密

**资源库提醒**

源码体积很小，配套资源库很大（数十 GB），通过百度网盘单独分发。**如果你只是想学习框架，请不要下载资源库**：代码可以配合任意自有数据目录运行，读懂架构完全不需要游戏资源。确需完整离线运行时，再按 [`docs/RESOURCES.md`](docs/RESOURCES.md) 获取。

## Security

- Never commit API keys, master keys, account passwords, or `_keys/` contents.
- The AI assistant can call local tools; treat its console account as privileged.
- Third-party assets (game data, music, video, Live2D models) keep their original rights and are **not** covered by this repository.

## License and disclaimer

Source code is published for learning and reference. It is provided **as-is**, without warranty of any kind. Blue Archive and all related assets belong to their respective owners; this project is unaffiliated and non-commercial.
