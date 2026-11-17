# PiAgentStation

Pi 的客户端。形态是一个中心加多个薄客户端：中心跑 Pi、管会话，PC 上的桌面窗口和手机上的浏览器
连的是同一个中心，看到的也是同一份会话。

## 现在能做什么

- 中心侧列出本机所有 Pi 会话，按工作目录分组；客户端点开就续聊。
- 流式显示正文与思考过程，思考块默认收起。
- 工具调用显示成卡片：状态、工具名、参数摘要、耗时，展开能看参数与输出。
- 流式期间按钮变「停止」，回车把消息插进队列（steer）。
- 切模型、切思考等级、看上下文用量、手动压缩。
- 扩展推的状态、组件、通知、选择框都能显示。
- 同一个会话被 PC 和手机同时打开时共用一个 Pi 子进程，事件广播给所有客户端。

## 怎么跑

需要中心那台机器装好 Pi（`~/.pi/agent`）与 .NET 10 SDK。客户端需要 WebView2 运行时。

```powershell
pwsh -File scripts/dev-run.ps1        # 起中心 + 桌面客户端
```

中心会在 `%LOCALAPPDATA%\MAStation\token.txt` 生成访问令牌，客户端自动读取。
想隔离会话记录，不污染真实会话：

```powershell
pwsh -File scripts/dev-run.ps1 -SessionDir E:\tmp\pigui-sessions
```

手机上打开 `http://<中心地址>:8787/?token=<令牌>` 即可，和桌面看到的是同一份会话。
默认只监听本机；要让别的设备连，得显式指定：

```powershell
pwsh -File scripts/dev-run.ps1 -Listen http://0.0.0.0:8787
```

这一步等于把中心那台机器上 Pi 的 shell 对局域网开放，只在可信网段做，令牌别外发。

## 调试

前端是普通 HTML/CSS/JS，没有构建步骤。`dev-run.ps1` 会开 9222 调试端口：

```powershell
node scripts/ui-smoke.mjs --cwd E:\tmp\pigui-smoke-ws   # 主链路冒烟，会真调一次模型
```

日志在 `%LOCALAPPDATA%\PiAgentStation\logs\`。桌面整屏截图抓不到 WebView2 的画面，
要看界面用 CDP 的 `Page.captureScreenshot`。

## 结构

```text
src/MAStation/    中心：pi 子进程、会话扫描、WebSocket、托管前端
src/MAStation/wwwroot/   前端（桌面与手机共用）
src/PiAgentStation.Desktop/   客户端：WebView2 壳，只加载中心地址
src/Shared/Log.cs             两边共用的文件日志
```

## 限制

- 局域网与 TLS 还没做，现在适合「中心在本机」或可信网段。
- 手机端只是同一页面能打开，窄屏布局还没调。
- `@文件` 补全、终端面板、会话重命名与删除还没做。
- 图片只作为附件随消息发送。
