# 构建与发布

## 发布前置

- 版本号写在两处：中心在 `src/SevenStation.Core/SevenStation.Core.csproj` 的 `<Version>`，
  客户端在 `src/SevenStation.Desktop/Cargo.toml` 的 `version`（Tauri 客户端是 Rust，没有 csproj）。
  三处一致才算对齐：两个文件 + 发布说明文件名。
- 发布说明必须存在：`docs/release-notes/v<版本>.md`。
- 发布前必须跑通：`dotnet build SevenStation.sln`，以及 `node scripts/ui-smoke.mjs --cwd <目录>`
  （中心与客户端先用 `scripts/dev-run.ps1` 起来）。

## 产物

两个产品各有自己的安装程序，可以装在不同机器上（见 `docs/DECISIONS.md` D7）。都不做便携版：
中心要靠服务起来、要有开机自启和升级路径，双击 exe 那种形态撑不住这些。

| 包 | 内容 | 命名 |
| --- | --- | --- |
| 中心 | MSI：装到 `%ProgramFiles%\SevenStation Core`，注册服务 `SevenStationCore`（自动启动、系统账户），开始菜单两个快捷方式 | `SevenStation-center-<版本>-win-x64.msi`（约 40 MB） |
| 客户端 | MSI：装到 `%ProgramFiles%\SevenStation`，装时填中心地址与令牌 | `SevenStation-client-<版本>-win-x64.msi` |

- 中心的安装程序：`powershell -File scripts/build-installer-center.ps1`（`-SkipPublish` 复用已有发布输出）。
  WiX 5 是 dotnet 全局工具（`dotnet tool install -g wix --version 5.0.2`），脚本会自己找 `~/.dotnet/tools/wix.exe`。
- 数据不进安装目录、也不随卸载删除：中心的配置、令牌、对话库、日志都在 `%ProgramData%\SevenStation.Core\`。
- 中心安装时不问 Pi 装在哪：服务第一次启动自己探测（`PI_AGENT_DIR` 优先，其次各用户目录下的 `~/.pi/agent`），
  写进 `settings.json`，之后按配置走；改配置用开始菜单的「打开中心配置」。
- 服务跑在系统账户下，但 Pi 的子进程会带上面板配置里记的用户家目录（`HOME`/`USERPROFILE`），
  所以 Pi 的密钥与会话仍在真实用户那边。
- 中心默认只监听 `127.0.0.1`。跨设备使用要改 `settings.json` 里的 `Listen` 并在防火墙放行。
- 两个包都是 Windows x64。中心以后会搬到 Linux 上跑，那时再加 Linux 包。

## 流程

1. 改两个版本号、写发布说明、跑 `python scripts/verify_release_notes.py --tag v<版本>`、提交。
2. 推 `main`。
3. 出包：`powershell -File scripts/build-installer-center.ps1`（客户端安装程序脚本待做），
   然后在一台干净环境里真装一遍、跑一遍，再卸载一遍。
4. 打标签 `git tag -a v<版本> -m "SevenStation <版本>"` 并推标签。
5. `gh release create v<版本> --title v<版本> --notes-file <拼好的正文> dist/*`（正文 = 发布说明 + `## 构建信息` 表）。
5. 已发布的标签不改写，修复走递增版本。

## 回滚

- 代码：`git revert`。
- 产物：留上一版的中心与客户端两个包及校验和，说明「中心与客户端版本必须一致」。
