# Resource Library / 资源库

English first, 中文在后。

---

## Before you download

The resource library is **not required to read or reuse this framework**. It contains tens of gigabytes of game assets (character art, voice, music, video, Live2D models). Download it only if you intend to run a complete offline archive locally.

If your goal is to study the architecture, skip this page, point the server at an empty directory, and read the source.

**Downloading the full pack just to look at the code is a waste of bandwidth and disk space.**

## Where it lives

The library is distributed through **Baidu Netdisk (百度网盘)**. Two profiles are supported:

| Profile | Contents | Size |
| --- | --- | --- |
| `Lite` | students, official gallery, game music, memory lobby, item icons, lossless music | ~54 GB |
| `Full` | everything above plus official PV, Bilibili videos, 3D models, model library | ~107 GB |

Pack and split locally with [`scripts/pack-resources.ps1`](../scripts/pack-resources.ps1):

```powershell
# Lite pack, 3.9 GB volumes
.\scripts\pack-resources.ps1 -Source E:\kivo_wiki -Dest D:\BAwiki-Resources -Profile Lite
```

> **Share link:** `<TO BE FILLED — paste the Baidu Netdisk share URL here>`
> **Extraction code:** `<TO BE FILLED>`
> **Pack version:** `<TO BE FILLED>`
> **SHA256:** see `SHA256SUMS.txt` produced next to the archives

The repository owner maintains this section after each pack is uploaded. Until a link is present, no public mirror exists.

Uploading ~50–100 GB is a manual, one-off step: run the script, upload the volumes to Baidu Netdisk, then fill in the fields above. The repository itself never stores assets.

## Expected layout

Extract the pack to a directory named `kivo_wiki` on a data drive. The server looks for these subdirectories:

```
kivo_wiki/
├─ 01_学生/            # character folders: _info.json, 立绘, 画廊, 皮肤, 语音, 技能
├─ 02_画廊/            # gallery images
├─ 03_游戏音乐/        # game music
├─ 04_官方PV/          # official PV
├─ 05_B站视频/         # Bilibili videos
├─ 06_回忆大厅/        # memory lobby: lobby_meta.json, home_spines.json, lobby/, spines/
├─ 07_3D模型/          # 3D models
├─ 08_本地导入/        # local imports
└─ _本地维基/
   └─ index/           # generated index cache, ai_sessions/, ai_memory/
```

The server scans the data directory on startup, generates thumbnails, and writes its cache under `_本地维基/index`. You can safely delete the cache; it is rebuilt.

## Preparing or updating the pack

1. Pack only the asset directories; never include `_keys/`, `accounts.json`, `ai.json`, or any file containing credentials.
2. Split into volumes of 4 GB or less when the archive exceeds Baidu Netdisk's single-file limits.
3. Generate a checksum file and record it above:

```powershell
Get-FileHash .\BAwiki-Resources-*.7z* -Algorithm SHA256 |
  ForEach-Object { "$($_.Hash)  $($_.Path)" } | Set-Content SHA256SUMS.txt
```

4. Upload to Baidu Netdisk, create a share link with an extraction code, and replace the placeholders in this file.

## Running with a custom directory

The server probes common locations (`E:\kivo_wiki`, `D:\kivo_wiki`, `F:\kivo_wiki`, and paths relative to the executable). If none exist, it prompts for a full path on first run.

---

## 中文说明

**资源库不是运行框架的必需品。** 它包含角色立绘、语音、音乐、视频、Live2D 等数十 GB 的游戏资源，仅在你要完整离线运行资料站时才需要下载。

如果你只是想学习这套框架，请跳过本页：把服务端指向任意空目录，直接读源码即可。为了看代码而下载整套资源库，既浪费带宽也浪费磁盘。

**分发方式：百度网盘。** 分享链接、提取码、版本号与 SHA256 由仓库维护者在每次打包上传后填写在本页顶部的占位处。在链接填写之前，不存在公开镜像。

解压后目录名应为 `kivo_wiki`，包含 `01_学生`、`02_画廊`、`03_游戏音乐`、`04_官方PV`、`05_B站视频`、`06_回忆大厅`、`07_3D模型`、`08_本地导入` 与 `_本地维基`。服务端启动时自动扫描并生成索引缓存；缓存可随时删除并重建。

打包时请勿包含 `_keys/`、`accounts.json`、`ai.json` 等含凭据的文件。
