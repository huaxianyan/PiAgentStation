# 构建与发布

## 发布前置

- 版本号写在两处：中心在 `src/SevenStation.Core/SevenStation.Core.csproj` 的 `<Version>`，
  客户端在 `src/SevenStation.Desktop/Cargo.toml` 的 `version`（Tauri 客户端是 Rust，没有 csproj）。
  两处一致，产物文件名与发布说明里的版本号才跟着对。
- 发布说明写在 `docs/release-notes/v<版本>-core.md`（中心）与 `v<版本>-client.md`（客户端），
  两份都要过门禁：`python scripts/verify_release_notes.py --link-pattern '/blob/[^/\s]+/' docs/release-notes/*.md`
- 发布前必须跑通：`dotnet build SevenStation.slnx`，以及
  `node scripts/ui-smoke.mjs --cwd <目录>`（中心与客户端先用 `scripts/dev-run.ps1` 起来）。

## 产物

两个产品各有自己的安装程序，装在不同的机器上（见 `docs/DECISIONS.md` D7）。都不做便携版：
中心要靠服务起来、要有开机自启和升级路径，双击 exe 那种形态撑不住这些。

| 包 | 内容 | 命名 |
| --- | --- | --- |
| 中心 | MSI：装到 `%ProgramFiles%\SevenStation Core`，注册服务 `SevenStationCore`（自动启动、系统账户），开始菜单两个快捷方式 | `SevenStationCore-win-x64-<版本>.msi`（约 40 MB） |
| 客户端 | MSI：装到 `%ProgramFiles%\SevenStation`，开始菜单与桌面快捷方式；第一次打开走引导页填中心地址与令牌 | `SevenStation-win-x64-<版本>.msi`（约 2.8 MB） |

命名规则：`<项目名>-<平台>-<架构>-<版本>.msi`，只用 ASCII 字母、数字、`-` 和 `.`；
校验文件同名加 `.sha256`，内容是 `<哈希>  <文件名>`，`sha256sum -c` 能直接读。

- 中心：`powershell -File scripts/build-installer-center.ps1`（`-SkipPublish` 复用已有发布输出）。
- 客户端：`powershell -File scripts/build-installer-client.ps1`（`-SkipBuild` 复用已有 `target/release`）。
- 两个安装程序的许可页读 `installer/license.rtf`（GPLv3 说明），构建脚本用 `-d LicenseRtf=` 传进去。
- `scripts/build-release.ps1` 打的是便携 zip，只给本机试跑用，正式发布不发。
- WiX 5 是 dotnet 全局工具（`dotnet tool install -g wix --version 5.0.2`），脚本会自己找 `~/.dotnet/tools/wix.exe`。
- 数据不进安装目录、也不随卸载删除：中心的配置、令牌、对话库、日志都在 `%ProgramData%\SevenStation.Core\`。
- 客户端的连接信息只放在本机 `%LOCALAPPDATA%\SevenStation\center.json`（带 `userSet` 标记），
  安装程序不写注册表，改地址不用重装。
- 中心安装时不问 Pi 装在哪：服务第一次启动自己探测（`PI_AGENT_DIR` 优先，其次各用户目录下的 `~/.pi/agent`），
  写进 `settings.json`，之后按配置走；改配置用开始菜单的「打开中心配置」。
- 服务跑在系统账户下，但 Pi 的子进程会配上配置里记的用户家目录（`HOME`/`USERPROFILE`），
  技能、扩展、会话都在真实用户那边，中心读这些目录也走同一份家目录。
- 中心默认只监听 `127.0.0.1`。跨设备使用要改 `settings.json` 里的 `Listen` 并在防火墙放行。
- 两个包都是 Windows x64。中心以后会搬到 Linux 上跑，那时再加 Linux 包。

## 流程

1. 改两个版本号，写两份发布说明，跑门禁，提交。
2. 推 `main`。
3. 出包：两个安装程序脚本各跑一遍，在一台干净环境里真装、真跑、再卸载。
4. 建两个 Release，各打一个标签：

   ```bash
   gh release create SevenStationCore-win-x64-<版本> \
     dist/SevenStationCore-win-x64-<版本>.msi \
     dist/SevenStationCore-win-x64-<版本>.msi.sha256 \
     --title SevenStationCore-win-x64-<版本> \
     --notes-file build/release/core.md

   gh release create SevenStation-win-x64-<版本> \
     dist/SevenStation-win-x64-<版本>.msi \
     dist/SevenStation-win-x64-<版本>.msi.sha256 \
     --title SevenStation-win-x64-<版本> \
     --notes-file build/release/client.md
   ```

   正文 = 发布说明 + `## 构建信息` 表（版本、平台、架构、源码提交、产物 SHA-256），删掉 `.wixpdb` 之类的调试符号。
5. 复核线上产物名、大小、校验和，确认没有显示标签。
6. 已发布的标签不改写，修复走递增版本。

## 回滚

- 代码：`git revert`。
- 产物：留上一版的中心与客户端两个包及校验和，说明「中心与客户端版本必须一致」。
