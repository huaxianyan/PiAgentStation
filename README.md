# PiAgentStation

PiAgentStation 是 Pi 的桌面客户端：一个 .NET 窗口托管 WebView2，进程里跑 `pi --mode rpc`
子进程，界面直接渲染 Pi 的会话事件。会话记录还是 Pi 自己那一份，不复制、不迁移。

## 现在能做什么

- 侧栏列出本机所有 Pi 会话，按工作目录分组，点一下续聊。
- 流式显示正文与思考过程，思考块默认收起。
- 工具调用显示成卡片：状态、工具名、参数摘要、耗时，展开能看参数与输出。
- 流式期间按钮变「停止」，回车把消息插进队列（steer）。
- 切模型、切思考等级、看上下文用量、手动压缩。
- 扩展推的状态、组件、通知、选择框都能显示。

## 怎么跑

需要本机已经装好 Pi（`~/.pi/agent`），以及 .NET 10 SDK。

```powershell
pwsh -File scripts/dev-run.ps1        # 杀掉旧实例、构建、带调试端口启动
```

想隔离会话记录，不污染真实会话：

```powershell
pwsh -File scripts/dev-run.ps1 -SessionDir E:\tmp\pigui-sessions
```

## 调试

界面是普通 HTML/CSS/JS，没有构建步骤。`dev-run.ps1` 会开 9222 调试端口：

```powershell
node scripts/ui-smoke.mjs --cwd E:\tmp\pigui-smoke-ws   # 主链路冒烟，会真调一次模型
```

运行日志在 `%LOCALAPPDATA%\PiAgentStation\logs\app.log`。桌面整屏截图抓不到 WebView2 的画面，
要看界面就用 CDP 的 `Page.captureScreenshot`。

## 结构

```text
src/PiAgentStation/MainWindow.xaml.cs   窗口与 WebView2 初始化、目录选择框、外链处理
src/PiAgentStation/AppBridge.cs         界面命令与事件的路由
src/PiAgentStation/Rpc/PiSession.cs     pi --mode rpc 子进程与 JSONL 协议
src/PiAgentStation/Sessions/           会话文件扫描
src/PiAgentStation/wwwroot/            界面：index.html、styles.css、app.js
```

## 限制

- `@文件` 补全、终端面板、会话重命名与删除还没做。
- 图片只作为附件随消息发送。
- 不检测同一会话是否被终端里的 Pi 占用。
