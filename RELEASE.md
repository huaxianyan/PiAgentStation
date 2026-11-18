# 构建与发布

## 发布前置

- 版本号写在两处：中心在 `src/SevenStation.Core/SevenStation.Core.csproj` 的 `<Version>`，
  客户端在 `src/SevenStation.Desktop/Cargo.toml` 的 `version`（Tauri 客户端是 Rust，没有 csproj）。
  三处一致才算对齐：两个文件 + 发布说明文件名。
- 发布说明必须存在：`docs/release-notes/v<版本>.md`。
- 发布前必须跑通：`dotnet build SevenStation.sln`，以及 `node scripts/ui-smoke.mjs --cwd <目录>`
  （中心与客户端先用 `scripts/dev-run.ps1` 起来）。

## 产物

两个包，版本号一致：

| 包 | 内容 | 命名 |
| --- | --- | --- |
| 中心 | `dotnet publish src/SevenStation.Core -c Release -r win-x64 --self-contained` 的输出，外加 `启动中心.cmd` | `SevenStation-center-<版本>-win-x64.zip`（约 48 MB） |
| 客户端 | 只有 `src/SevenStation.Desktop/target/release/SevenStation.exe` 一个文件 | `SevenStation-client-<版本>-win-x64.zip`（约 3 MB） |

客户端只发那一个 exe：`target/release` 里还有 `deps/`、`build/`、`.pdb` 这些构建中间产物，
整个压进去是 380 MB 左右。脚本会先拷到暂存目录再压，包超过 40 MB 直接报错。

- 打包走 `pwsh -File scripts/build-release.ps1`：校验两个版本号一致、要求发布说明存在、
  发布两个自包含包、压成上面两个 zip，并在 `dist/` 里各写一个 `.sha256`（`sha256sum -c` 可直接验）。
- 落点：`dist/`（被忽略，不入库）。中心包里带一个 `启动中心.cmd`，双击即起。
- 功能没做完之前只留脚本与发布说明草稿，不打标签、不建 Release。
- 中心是自包含的，没装 .NET 的机器也能跑；客户端是单个原生 exe，仍要求系统有 WebView2 运行时
  （Win11 自带，Win10 大多数也带）。
- 两个包都是 Windows x64。中心以后会搬到 Linux 上跑，那时再加 Linux 包；现在的脚本只产 Windows。
- 会话与配置不进产物。首次运行写 `%LOCALAPPDATA%\SevenStation\`：WebView2 缓存、日志、
  中心令牌、客户端地址配置。
- 中心默认只监听 `127.0.0.1`。发布说明里要写清怎么用 `SEVENSTATION_LISTEN` 开放局域网，
  以及「等于把这台机器的 Pi shell 对外」这条风险。

## 流程

1. 改两个版本号、写发布说明、跑 `python scripts/verify_release_notes.py --tag v<版本>`、提交。
2. 推 `main`。
3. `pwsh -File scripts/build-release.ps1` 出包，本地解压跑一遍。
4. 打标签 `git tag -a v<版本> -m "SevenStation <版本>"` 并推标签。
5. `gh release create v<版本> --title v<版本> --notes-file <拼好的正文> dist/*`（正文 = 发布说明 + `## 构建信息` 表）。
5. 已发布的标签不改写，修复走递增版本。

## 回滚

- 代码：`git revert`。
- 产物：留上一版的中心与客户端两个包及校验和，说明「中心与客户端版本必须一致」。
