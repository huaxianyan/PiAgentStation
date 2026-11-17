# 进度

## 当前主线

- 现在在做什么：对话归 MAStation 所有，存在本地 SQLite 里；客户端只读这份数据，看历史不经过 Pi。
  MA 按需跟 Pi 建会话并绑定，每轮结束把消息取回来存档。
- 下一步：手机端能用（现在只是同一页面能打开，还要按窄屏调）、中心常驻运行（开机自启或服务化）、
  `@文件` 补全、终端面板、会话重命名与删除。
- 已知阻塞：无。中心默认只监听本机，局域网开放要用户明确授权。

## STATUS-001：Pi 桌面界面能开会话并流式对话

- 做了什么：.NET 10 WPF 窗口托管 WebView2；C# 侧管 `pi --mode rpc` 子进程，一条命令一个
  相关响应、事件原样转发给页面；界面实现会话列表（按工作目录分组）、历史渲染、流式正文与
  思考块、工具卡片、扩展弹窗与状态栏、模型与思考等级切换、上下文用量与压缩。
- 验证方式（2026-11-17，真窗口 + CDP 驱动真实交互）：
  - 冷启动到 `get_state` 响应 4.4 秒，关 stdin 后 0.25 秒退出，退出码 0。
  - 新建会话、发送 prompt、首个 `text_delta` 到 `agent_settled` 全通，正文与思考块落在页面上。
  - 点击侧栏已有会话可续聊：历史 7 条消息、工具卡片 1 张（bash，105 毫秒）渲染正确。
  - 模型下拉 17 项、思考等级、上下文用量（1% · 18k/1000k）都来自真实响应。
  - 扩展状态栏能显示 Pi 扩展推的 `setStatus`，ANSI 色码已剥掉。
- 边界：图片只作为附件随文本发送，`@文件` 补全没做；终端面板没做；扩展弹窗只在
  select / confirm / input / editor 四种上验证了渲染路径，没有真实扩展触发过全流程。
- 证据：`dotnet build` 0 警告 0 错误；界面截图在当时的临时目录，未入库。

## STATUS-002：仓库定名 PiAgentStation 并推到公开远端

- 按用户要求把项目搬到当前工作目录，仓库名与目录同名（均为 PiAgentStation），
  程序集、命名空间、exe、日志与界面品牌一并改名，避免同一产品留两个名字。
- 验证：`dotnet build` 通过，`scripts/ui-smoke.mjs` 在改名后重跑通过；`git log` 保留原有提交。
- 记录：那次 `gdi32full.dll` 崩溃（`0xc0000409`）由用户指认为本机 MacType 注入所致，
  不是代码问题，已写进 `AGENTS.md` 的已知坑。

## STATUS-003：拆成中心 + 客户端，多端共用一份会话

- 用户 2026-11-17 定下目标形态：一套中心负责跟 Pi 打交道，PC 与手机都连这套中心；日后客户端再加
  host 能力，用中心的 Pi 操作别的设备。原「只做 PC 端」的约束作废，见 `docs/DECISIONS.md`。
- 改了什么：新增 `src/MAStation`（ASP.NET Core，管 pi 子进程、会话扫描、`/ws`、
  托管前端）；桌面端退化成 WebView2 壳，加载中心地址；`AppBridge` 变成 `CenterHub`，
  命令应答回发起连接、会话事件广播给所有连接。前端从 `chrome.webview` 桥换成 WebSocket。
- 安全：中心默认只监听 `127.0.0.1:8787`；`/ws` 要令牌，令牌文件在 `%LOCALAPPDATA%\MAStation\token.txt`。
- 验证（2026-11-17，真窗口 + CDP + 第二个 WebSocket 客户端）：
  - `scripts/ui-smoke.mjs` 通过：连接状态「已连接」、新会话、流式、收尾、侧栏刷新。
  - 第二个客户端 `list_sessions` 拿到同一份会话（1 条），`open_session` 拿到 3 条历史。
  - 桌面窗口里发一条消息，第二个客户端收到同一条 `text_delta` 与 `agent_settled`，key 一致。
  - 同一会话打开两次只产生 1 个 key，即一个子进程，满足单写者。
- 边界：局域网与 TLS 没做；手机端只验证了同一页面能被打开（未做窄屏适配）。

## STATUS-004：中心定名 MAStation，与客户端分家

- 用户 2026-11-17 定名：中心叫 MAStation（MA 既是 Main 也是 Main Agent），客户端仍是 PiAgentStation。
- 改了什么：`src/PiAgentStation.Server` → `src/MAStation`，程序集与 exe 变成 `MAStation`；
  中心日志写 `%LOCALAPPDATA%\MAStation\logs\center.log`，令牌文件 `%LOCALAPPDATA%\MAStation	oken.txt`；
  环境变量改为 `MASTATION_LISTEN` / `MASTATION_TOKEN`；客户端从中心的令牌文件自动读取。
- 界面：连接状态显示成「已连接 · MAStation」；新建会话语义改为「填中心机器上的目录」；
  空状态文案不再说「本机」。
- 验证：`dotnet build PiAgentStation.slnx` 0 警告 0 错误；`dev-run.ps1` 起中心与客户端，
  health 返回真实会话根目录，界面连上后列出 170 个会话。

## STATUS-005：对话归 MAStation，客户端只读 MA 的数据

- 用户 2026-11-17 定：不再全盘使用 Pi 的会话管理。Pi 的会话目录太乱（一百多条结束未清理），
  点开旧会话要等很久、还有加载失败的。对话改成 MAStation 自己的概念，客户端只跟 MA 通讯。
  记在 `docs/DECISIONS.md` 的 D5。
- 数据：SQLite（`%LOCALAPPDATA%\MAStation\data\mastation.db`，`MASTATION_DATA_DIR` 可覆盖），
  两张表 `conversations` / `messages`，加一张 `settings` 放模型列表这类缓存。
- 绑定：新建对话时只建一条记录；第一次发消息才起 Pi 子进程，把 Pi 的 `sessionFile` 记进
  `pi_session_file` 字段，续聊时拿它 resume。这个字段不出现在任何客户端响应里。
- 存档：每轮 `agent_settled` 后取一次 `get_messages` / `get_session_stats` / `get_state`，
  整体替换该对话的消息并更新标题、模型、思考等级、用量。
- 验证（2026-11-17，真窗口 + CDP + SQLite 直查）：
  - 新建对话 → 发消息 → 流式 → 收尾，库里落地 5 条消息（含 1 条 system）、标题取首句、
    `pi_session_file` 已写入。
  - 关掉中心（子进程一起没）再开，点开同一对话：109 ms 出历史，**没有**起任何 Pi 子进程。
  - 模型列表改为读缓存后，看历史的路径完全不碰 Pi。
  - 删除对话：侧栏 2 → 1，库里对应行与消息一起消失，Pi 那边文件保留。
  - `scripts/ui-smoke.mjs` 通过（用量 `1.1% · 11k/1000k`）。
- 顺带修：`refresh_stats` 原来回的是整份状态，收尾时会用库里未写完的副本覆盖刚流式出来的内容；
  现在只回用量。SQLite 原生库有已知高危版本，已钉 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5。
- 边界：旧的 170 条 Pi 会话不迁移，MA 从空库开始；对话不能重命名（标题自动取首句）。

## 归档索引
