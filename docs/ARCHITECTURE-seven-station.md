# Seven Station：目标形态与迁移计划

## 定位

Seven Station 是一个本机单用户的 agent 工作口。Host 是唯一的管理者，Agent 是跑在 Host 里的工作执行者，
客户端只是入口。

- **Host 只有一份。**它管理所有工作（work）的生命周期，持有与 Agent 的连接，把同一份工作给多个入口看和操作。
  客户端断开、换设备、重开，工作照旧。
- **Agent 可扩展。**Pi 是当前最重要、很长一段时间里唯一的 Agent 实现，但 Host 不写死 Pi：
  以后接别的 Agent，Host 的管理方式不变，客户端按 Agent 声明支持的能力显示界面。
- **客户端是入口。**网页、桌面、以后的手机，都通过同一套协议连 Host，不直连 Agent。

参考形态来自 alu-dock（`aruru-project/alu-dock`）：host / worker / client 三层，Pi 作为 worker 的一种实现，
官方客户端是跨 Agent 的入口。

## 命名

| 现在 | 目标 |
| --- | --- |
| 产品 SevenStation | 产品 Seven Station |
| 仓库 `SevenStation` | 仓库 `SevenStation` |
| `src/SevenStation.Core`（中心） | `src/SevenStation.Host` |
| `src/SevenStation.Desktop`（WPF 壳） | `src/SevenStation.Desktop`（Tauri 壳，重写） |
| `%LOCALAPPDATA%\SevenStation\`（已改，中心和客户端同一个目录） | `%LOCALAPPDATA%\SevenStation\` |
| `SEVENSTATION_LISTEN` / `SEVENSTATION_TOKEN` | `SEVENSTATION_LISTEN` / `SEVENSTATION_TOKEN` |

中心这个词在文档里换成 Host，界面文案里仍只说「中心」或「服务」，不出现工程词。

## 分层

### Host（现在的 SevenStation.Core）

- 持有工作列表与生命周期：新建、恢复、停止、重命名、删除。
- 单写者：一份工作只有一个 Agent 进程在跑，多个客户端共享同一份事件流。
- 持久化：工作与会话记录在本地 SQLite，客户端看历史不经过 Agent。
- Agent 注册：知道有哪些 Agent 可用、每个支持什么。
- 客户端接入：WebSocket + 令牌，事件广播给所有连接。
- 之后：客户端配对、多机器 Host。

### Agent

Host 里给 Agent 留的边界：

- `IAgentRuntime`：启动 / 停止一份工作，发送输入，中断，列模型与思考等级，列命令与技能，
  处理扩展交互，取会话历史，报能力。
- `AgentCapabilities`：这份 Agent 支持什么（工具卡片、扩展交互、压缩、上下文用量、思考等级……）。
  客户端按能力表渲染，不按 Agent 名字写分支。
- Pi 的实现放在 `Agents/Pi`，Pi 专属的东西（`pi --mode rpc` 子进程、`models.json`、扩展与技能的扫描）
  都留在这一层，不上浮到 Host 的调度里。

### Client

- **Web**：Host 直接托管的那套页面，浏览器打开即用，也是手机端当前唯一入口。
- **Desktop**：Tauri 壳（Rust + WebView2），加载 Host 的页面，负责窗口、托盘、原生选目录、单实例、
  开机自启这类壳层能力。窗口用系统边框，跟 alu-dock 一样，不自绘标题栏。
- 以后：Android 客户端、TUI。

## 迁移阶段

1. ~~改名~~ **已完成**：解决方案、项目、程序集、exe、命名空间、数据目录（`%LOCALAPPDATA%\SevenStation`）、
   环境变量（`SEVENSTATION_*`）、日志前缀、界面品牌、仓库名都换了。本地目录还剩最后一步（见交接）。
2. **Host 拆出 Agent 边界**（进行中）：`Agents/AgentDescriptor.cs` 与 `Agents/PiAgent.cs` 已落地，
   协议 `hello` 里改成 `agent` + `agents` 名片（id、名称、版本、是否就绪、能力表、信息行），
   设置页按名片渲染，思考等级与模型面板按能力表决定显不显示。
   还没做：把 `CenterHub` 里 Pi 的会话生命周期收进 `IAgentRuntime` 实现，现在仍是 Host 直接起 `PiSession`。
3. ~~桌面壳换 Tauri~~ **已完成**：`src/SevenStation.Desktop` 是 Tauri 2（Rust），负责加载中心页面、
   记忆窗口位置、离线提示、日志、系统选目录；窗口用系统标题栏，页面上的三个自绘按钮已去掉。
4. **之后的活**（不在本轮）：客户端配对、手机端窄屏适配、多机器 Host。

## 前置条件

- Rust 工具链（当前机器没有 `rustc` / `cargo`）。VS2017 BuildTools 里有 MSVC 14.16，但没有 C++ 命令行在 PATH，
  也版本偏旧。需要装 rustup，并配一套新的 MSVC Build Tools（或改用 GNU 工具链）。
- Node.js 已有（v22），Tauri 前端不需要额外构建链，页面沿用现在的纯 HTML/CSS/JS。

## 不变的部分

- Pi 的配置与会话仍然归 Pi 自己（`~/.pi/agent/sessions`），Host 不复制、不改写。
- 界面继续以 `design/prototype.html` 为准。
- 本机单用户，不向云端多用户平台扩展。
