# Architecture

A short map of the framework. English first, 中文摘要 at the end.

## Process model

```
BAwikiLauncher.exe (native, C++)
 └─ BAwikiWeb.exe (single-file .NET 8, HttpListener)
     ├─ static frontend  →  web/
     ├─ index cache      →  kivo_wiki/_本地维基/index/
     ├─ media streaming  →  kivo_wiki/{02_画廊,03_游戏音乐,...}
     ├─ AI assistant     →  DeepSeek-compatible chat API + local tools
     ├─ LDTE             →  training archives, encrypted clips, keys
     └─ console commands →  AR / RI / ST / AI / HELP / Q
```

The launcher handles elevation and diagnostics, pipes the server's stdout into its own window and `launcher.log`, and restarts nothing automatically.

## Server

| Module | Responsibility |
| --- | --- |
| `BAwikiWeb.cs` | Startup, data-directory discovery, routing, auth, accounts, sessions, static/media serving, console commands |
| `Server/Server.Cache.cs` | Binary index cache, thumbnail scheduling |
| `Server/Server.Online.cs` | kivo.wiki update pipeline: audit, download, lobby/Spine sync |
| `Server/Server.Import.cs` | Local audio/video import, FFmpeg probing |
| `Server/Server.Ldte.cs` | LDTE writer/reader, clip archiving, master key, temp keys, cloud-key proxy |
| `Server/Server.Ai.cs` | Assistant, tool contract, sessions, per-account history |
| `Server/Server.Plugin.cs` | Plugin discovery and manifest handling |
| `Server/Server.Profile.cs` | Per-user UI/profile preferences |
| `Server/Server.Gpu.cs` | GPU information |
| `Server/Server.Parse.cs` | Offline data parsing helpers |

Authentication uses an account file plus signed session cookies. Local requests are treated as the built-in `admin` account; external requests must present a session or a pairing token.

## Frontend

`web/` is plain HTML/CSS/JS — no bundler, no framework.

- `index.html` + `assets/app.js`: gallery, student pages, music player, PV, video, lobby, settings.
- `assets/audio-lab.js`: WebAudio processing chain (EQ, dynamics, limiter, training-data correction).
- `assets/engine/lobby-engine.js`: Live2D/Spine lobby renderer.
- `admin.html`: operations console — accounts, cache, GPU, plugins, LDTE keys, AI console, conversation audit.

Caching is controlled with `?v=` query strings on asset URLs; bump them when changing files.

## LDTE audio format

LDTE is a compact per-frame analysis archive.

```
Header (v2, 48 bytes)
  magic "LDTE" | version u16 | frameMs u16 | passes u32 | frames u32
  jsonLen u32 | channels u32 | sampleRate u32 | flags u32 | segCount u32 | reserved 12B
Header JSON (jsonLen bytes, UTF-8)
  key, name, updated, system, tracks, stats, profiles, segments, limit
Channel data
  channels × frames × float32, column-major
```

Channels: `peak, rms, zcr, flux, bright, low, mid, high, width, corr, trans, clip` at 16 ms per frame.

Derived profiles: `calibration`, `timbre`, `dynamics`, `stereo`, `decoder`, `dolby`.

LDTC1 encrypted clips use a double-layer AES-256-GCM envelope:

```
"LDTC1" | ver 1B | nonce 12B | tag 16B | wnonce 12B | wtag 16B | wkey 32B | len i64 | ciphertext
```

The data key is wrapped by the 32-byte master key. Cloud decryption uses a `CFK1.<id>.<exp>.<hmac>` token issued by the Worker; the master key never leaves the server side.

## Cloudflare Worker

`cloudflare-worker/` implements four endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /health` | liveness |
| `POST /issue` | admin-signed token issue (`x-admin-secret`) |
| `GET /verify?token=` | token validation |
| `POST /revoke` | revoke by id |
| `POST /decrypt` | decrypt an LDTC1 clip with the Worker's master key |

Secrets live in Worker environment variables (`BA_ADMIN_SECRET`, `BA_TOKEN_SECRET`, `LDTE_MASTER_KEY`), never in code.

## Data flow

1. Launcher starts server; server locates `kivo_wiki`, loads or rebuilds the index.
2. Browser requests pages and media from `127.0.0.1`.
3. Admin-triggered update audits local files against the remote catalog, downloads only missing pieces, then rebuilds the index.
4. Music training writes LDTE archives; encrypted clips are archived with LDTC1 for later playback.

---

## 中文摘要

**进程结构**：原生启动器负责提权与日志，拉起单文件 .NET 8 服务端；服务端用 HttpListener 提供静态页面、媒体流、索引、AI、LDTE 与在线更新。

**服务端模块**：缓存与缩略图、在线更新、导入、LDTE、AI、插件、用户配置、GPU、解析，各自独立文件，通过 `partial class Server` 组合。

**前端**：无框架的原生 HTML/CSS/JS；`audio-lab.js` 负责 WebAudio 处理链，`lobby-engine.js` 负责 Live2D/Spine 大厅。

**LDTE**：48 字节固定头 + JSON 头 + 12 通道 float32 数据，16 ms/帧；派生校准、音色、动态、立体声、解码器、杜比六类档案。

**LDTC1**：双层 AES-256-GCM；数据密钥由 32 字节主密钥包裹，云端只持有主密钥并由 `CFK1` 令牌授权。

**Worker**：`/health`、`/issue`、`/verify`、`/revoke`、`/decrypt` 五个端点，密钥只存在于环境变量。
