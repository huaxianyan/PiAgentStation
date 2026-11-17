# 构建与发布

## 发布前置

- 版本号写在两个 csproj 里：`src/PiAgentStation.Server/PiAgentStation.Server.csproj` 与
  `src/PiAgentStation.Desktop/PiAgentStation.Desktop.csproj`，两处一起改。
- 发布说明必须存在：`docs/release-notes/v<版本>.md`。
- 发布前必须跑通：`dotnet build PiAgentStation.sln`，以及 `node scripts/ui-smoke.mjs --cwd <目录>`
  （中心与客户端先用 `scripts/dev-run.ps1` 起来）。

## 产物

两个包，版本号一致：

| 包 | 内容 | 命名 |
| --- | --- | --- |
| 中心 | `dotnet publish src/PiAgentStation.Server -c Release -r win-x64 --self-contained` 的输出 | `PiAgentStation-center-<版本>-win-x64.zip` |
| 客户端 | `dotnet publish src/PiAgentStation.Desktop -c Release -r win-x64 --self-contained` 的输出 | `PiAgentStation-client-<版本>-win-x64.zip` |

- 落点：`dist/`（被忽略，不入库）。每个 zip 同时产出 `.zip.sha256`。
- 自包含是为了让没装 .NET 的机器也能跑；客户端仍要求系统有 WebView2 运行时。
- 会话与配置不进产物。首次运行写 `%LOCALAPPDATA%\PiAgentStation\`：WebView2 缓存、日志、
  中心令牌、客户端地址配置。
- 中心默认只监听 `127.0.0.1`。发布说明里要写清怎么用 `PIAGENTSTATION_LISTEN` 开放局域网，
  以及「等于把这台机器的 Pi shell 对外」这条风险。

## 流程

1. 改两个版本号、写发布说明、提交。
2. 推 `main`。
3. 打标签 `git tag -a v<版本> -m "PiAgentStation <版本>"` 并推标签。
4. 构建、跑冒烟、打两个包、建 Release；发布说明用 `gh release edit --notes-file` 换成正式版。
5. 已发布的标签不改写，修复走递增版本。

## 回滚

- 代码：`git revert`。
- 产物：留上一版的中心与客户端两个包及校验和，说明「中心与客户端版本必须一致」。
