# SevenStation

Pi 的客户端。形态是一个 Seven Station Core 加多个薄客户端：Seven Station Core 跑 Pi、管会话，PC 上的桌面窗口和手机上的浏览器
连的是同一个 Seven Station Core，看到的也是同一份会话。

## 现在能做什么

- Seven Station Core 侧列出本机所有 Pi 会话，按工作目录分组；客户端点开就续聊。
- 流式显示正文与思考过程，思考块默认收起。
- 工具调用显示成卡片：状态、工具名、参数摘要、耗时，展开能看参数与输出。
- 流式期间按钮变「停止」，回车把消息插进队列（steer）。
- 切模型、切思考等级、看上下文用量、手动压缩。
- 扩展推的状态、组件、通知、选择框都能显示。
- 同一个会话被 PC 和手机同时打开时共用一个 Pi 子进程，事件广播给所有客户端。

## 装起来用

两个安装程序，都要管理员权限（Seven Station Core 要注册系统服务），版本号要一致：

| 包 | 装在哪 | 装完是什么样 |
| --- | --- | --- |
| `SevenStationCore-win-x64-<版本>.msi` | Seven Station Core 那台机器 | 注册服务 `SevenStationCore`，开机自启，只监听 `127.0.0.1:8787` |
| `SevenStation-win-x64-<版本>.msi` | 要用的机器 | 开始菜单与桌面一个快捷方式，第一次打开是「先连上 Seven Station Core」引导页 |

先装 Seven Station Core，再装客户端。客户端第一次打开会让你填 Seven Station Core 地址和访问令牌，令牌在 Seven Station Core 那台机器的
`%ProgramData%\SevenStation.Core\token.txt`。填完随时可以在「设置 → 这台设备连的 Seven Station Core」里改，
保存就重连。

Seven Station Core 那台机器还要有 Pi（`~/.pi/agent`）；服务第一次启动会自己找到它，写进
`%ProgramData%\SevenStation.Core\settings.json`，改配置用开始菜单里的「打开 Seven Station Core 配置」。

## 开发

Seven Station Core 那台机器要 .NET 10 SDK，桌面客户端要 Rust 工具链（cargo）与 WebView2 运行时。

```powershell
pwsh -File scripts/dev-run.ps1 # 构建并起 Seven Station Core + 桌面客户端，开 9222 调试端口
```

只想起停 Seven Station Core、不做构建：

```powershell
pwsh -File scripts/start-center.ps1 # 起 Seven Station Core
pwsh -File scripts/stop-center.ps1 # 停
```

开发时的令牌在 `%LOCALAPPDATA%\SevenStation\token.txt`，客户端自动读取。想隔离会话记录、
不污染真实会话：

```powershell
pwsh -File scripts/dev-run.ps1 -SessionDir E:\tmp\pigui-sessions
```

手机上打开 `http://<Seven Station Core 地址>:8787/?token=<令牌>`，和桌面看到的是同一份会话。默认只监听本机，
要让别的设备连需要显式放开：

```powershell
pwsh -File scripts/dev-run.ps1 -Listen http://0.0.0.0:8787
```

这一步等于把 Seven Station Core 那台机器上 Pi 的 shell 对局域网开放，只在可信网段做，令牌别外发。

## 输入区能干什么

- `@` 补全工作区里的文件，选到目录会接着往下钻
- `/` 列出 Pi 里的命令（扩展命令、提示词模板、技能），按工作区缓存
- 图片可以直接粘、也可以拖进窗口
- 代码块右上角「复制」；头部「导出为 Markdown」把整段对话存下来
- 头部终端图标打开一个常驻 PowerShell，就在当前工作区里跑，变量与目录都能延续；
  没有 PTY，`vim`、`top` 这类全屏程序用不了
- 头部文件夹图标打开工作区文件：只读浏览与预览，路径走不出工作区
- 侧栏搜索会连正文一起搜，结果里带命中的那一段
- 快捷键：`Ctrl+K` 新建、`Ctrl+B` 侧栏、`Ctrl+J` 终端、`Ctrl+E` 文件、`Ctrl+F` 搜索

## 调试

前端是普通 HTML/CSS/JS，没有构建步骤。`dev-run.ps1` 会开 9222 调试端口：

```powershell
node scripts/ui-smoke.mjs --cwd E:\tmp\pigui-smoke-ws # 主链路冒烟，会真调一次模型
```

日志在 `%LOCALAPPDATA%\SevenStation\logs\`。桌面整屏截图抓不到 WebView2 的画面，
要看界面用 CDP 的 `Page.captureScreenshot`。

## 结构

```text
src/SevenStation.Core/ Seven Station Core：对话库、pi 子进程、WebSocket、托管前端
src/SevenStation.Core/Storage/ 对话库（SQLite）
src/SevenStation.Core/wwwroot/ 前端（桌面与手机共用）
src/SevenStation.Desktop/ 桌面客户端：Tauri 壳（Rust + WebView2），只加载 Seven Station Core 地址
src/Shared/Log.cs 两边共用的文件日志
```

## 发布

两个 Windows x64 安装程序，版本号一致：

| 包 | 是什么 | 大小 |
| --- | --- | --- |
| `SevenStationCore-win-x64-<版本>.msi` | Seven Station Core，自包含，没装 .NET 也能跑 | 约 40 MB |
| `SevenStation-win-x64-<版本>.msi` | 桌面客户端，单个 exe，要有 WebView2 运行时 | 约 2.8 MB |

两个包分开建 Release：`SevenStationCore-win-x64-<版本>` 与 `SevenStation-win-x64-<版本>`，
每个 Release 只装自己的包和同名 `.sha256`。Seven Station Core 以后会搬到 Linux 上用，那时再加 Linux 包。

发布说明写在 `docs/release-notes/`，打包与发布流程见 [RELEASE.md](RELEASE.md)。

## 限制

- 局域网与 TLS 还没做，现在适合「Seven Station Core 在本机」或可信网段。
- 手机端只是同一页面能打开，窄屏布局还没调。
- 图片只作为附件随消息发送（还没让模型直接读图）。

## 许可证

SevenStation 采用 GNU 通用公共许可证第 3 版（GPLv3）发布，许可证全文见 [LICENSE](LICENSE)。
