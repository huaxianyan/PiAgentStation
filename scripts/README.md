# 开发与验证脚本

两个脚本都只在本机用，不进发布产物。

## `dev-run.ps1`

杀掉正在跑的实例、构建、带调试端口启动。开发时每次都走它，省得手动记环境变量。

```powershell
pwsh -File scripts/dev-run.ps1
# 想隔离会话记录（不污染真实会话）时：
pwsh -File scripts/dev-run.ps1 -SessionDir E:\tmp\pigui-sessions
```

启动后可以用 `scripts/ui-smoke.mjs` 连上去检查界面。

## `ui-smoke.mjs`

用 CDP 驱动真实窗口跑一遍主链路：列表渲染 → 新建会话 → 发一条 prompt →
看到流式状态 → 收尾生效 → 正文非空。失败时打印页面异常并以非零码退出。

```bash
node scripts/ui-smoke.mjs --cwd E:\tmp\pigui-smoke-ws
```

前置条件：应用已经用 `--remote-debugging-port=9222` 启动（`dev-run.ps1` 会做）。
`--cwd` 指定新会话的工作目录，必须已存在。这个脚本会真的调用一次模型。
