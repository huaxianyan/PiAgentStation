# 构建与发布

## 发布前置

- 版本号写在 `src/PiGui/PiGui.csproj` 的 `<Version>`，功能走次版本、修复走修订版本。
- 发布说明必须存在：`docs/release-notes/v<版本>.md`。
- 发布前必须跑通：`dotnet build src/PiGui/PiGui.csproj`，
  以及 `node scripts/ui-smoke.mjs --cwd <目录>`（应用先用 `scripts/dev-run.ps1` 起来）。

## 产物

- 命名：`Pi-GUI-<版本>-win-x64.zip`。
- 落点：`dist/`（被忽略，不入库）。
- 校验：同时产出 `.zip.sha256`。
- 内容：`dotnet publish src/PiGui/PiGui.csproj -c Release -r win-x64 --self-contained` 的输出。
  自包含是为了让没装 .NET 的机器也能开；WebView2 运行时仍要求系统已安装。
- 会话与配置不进产物，首次运行写到 `%LOCALAPPDATA%\PiGui\`。

## 流程

1. 改版本号、写发布说明、提交。
2. 推 `main`。
3. 打标签 `git tag -a v<版本> -m "Pi-GUI <版本>"` 并推标签。
4. 构建、跑冒烟、打包、建 Release；发布说明用 `gh release edit --notes-file` 换成正式版。
5. 已发布的标签不改写，修复走递增版本。

## 回滚

- 代码：`git revert`。
- 产物：留上一个版本的 zip 与校验和。
