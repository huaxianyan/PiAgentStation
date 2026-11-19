# 进度

## 当前主线

- 现在在做什么：0.4.0 出包。相对 0.3.0 的增量是 Seven Station Control（并进 Seven Station Core 的 MSI）、
  安装程序自己的画面与图标、局域网连接开关、命名统一。
- 发布物仍是**两个 Windows x64 安装程序**：`SevenStationCore-win-x64-0.4.0.msi`（含 Control，43.3 MB）与
  `SevenStation-win-x64-0.4.0.msi`（3.2 MB）。都不做便携版。
- 已验完：两个 MSI 出包、本机 0.3.0 覆盖装到 0.4.0（服务重启、版本变 0.4.0、`%ProgramData%\SevenStation.Core`
  的令牌与设置一字不差、对话库 1.7 MB 保留）、`ui-smoke.mjs` 全绿。
- 顺手清了本机两个历史残留安装：一个旧 0.3.0 中心的 ProductCode（`{B0F57C2D…}`）与一个探针包（`0.0.1`）。
  这类残留会让「应用和功能」出现同名两条，`MajorUpgrade` 一次只清掉一条。
- README 与仓库描述按 `readme-standard` 重写过，截图在 `docs/images/`。
- **本机装着的 v0.4.0 是用户的生产环境**（2026-11-19 起他用这两个软件干活）：测试一律用
  `SEVENSTATION_PROFILE=test` + 临时 `SEVENSTATION_CORE_HOME` + 8899 端口，详见 `AGENTS.md` 的「生产与测试隔离」。
- 2026-11-19 起用户把日常开发搬到这里来用（正式版 `v0.4.0`）。装前备份在
  `%USERPROFILE%\SevenStation-backup-20261119-1458\`；工作区都在 Seven Station Core 那台机器上，
  客户端设备的本地文件不参与。
- 已发布（2026-11-19）：Release `v0.4.0` 一个页面，装 Seven Station Core 与客户端的两个 MSI 加各自 `.sha256`；
  0.3.0 也按同一形态重做成 `v0.3.0`。标签只有版本号，一版一 Release。
- 已知阻塞：无。
- 已知限制（写进发布说明）：Seven Station Core 目前只放 Windows 包，之后会出 Linux 版；局域网限本地子网、没做 TLS；
  手机窄屏未调；数据库没有版本化迁移，只有「查列再补」这一处临时逻辑。

## STATUS-032：可点位置双管齐下——渲染器兼容 + 给 Pi 的写法约定（2026-11-19，未发版）

- 渲染器（`wwwroot/app.js` 的 `decorateLinks`）：
  - 行内代码（反引号）里的路径也做成链接；只跳过 `a` 与代码块 `pre`。模型爱把路径包进反引号，这条解决了一大半。
  - 新增引号包裹的路径：`"E:\My Files\notes.md"` 整段识别，引号本身不进链接，也不会被句末标点裁剪。
  - 裸路径不再吞中文标点（`，。；：！？、（）【】《》「」『』`），否则「E:\a\b，然后」整句都成了链接。
  - 验证（node 逐条跑）：裸路径、带空格引号路径、反引号路径、UNC、网址带句号、中文逗号结尾、无路径的普通文字，七种输入结果都对。
- 给 Pi 的写法约定：新增 `src/SevenStation.Core/Agents/pi-guidance.md`（随程序集输出），
  `PiSession.Start` 用 `--append-system-prompt <内容>` 注入。Pi 的这个参数只吃文本，所以 Core 自己读文件再传。
- 兼容兜底：`PiLocator.SupportsAppendSystemPrompt` 先跑一次 `node <agent>\bin\pi-launcher.js --help`
  看有没有这个参数（老版本遇到不认识的参数会直接退出，不能让聊天整个挂掉），结果按进程缓存。
- 实测：
  - 子进程命令行里出现 `--append-system-prompt`（父进程是测试实例）→ `injected=YES`。
  - 问「把这两个文件的完整路径发给我」，回复就是裸绝对路径 + 裸网址，没有反引号也没有相对路径。
- 出包：`dist/SevenStationCore-win-x64-0.4.1.msi` 43.7 MB，SHA-256 `a103b25e6d5d4f26679926011a17ae1730a7dbea84dd8b020fca251b8febdeac`。
- 界面观感（链接在反引号里的样子）还没人眼确认过。

## STATUS-031：服务的 Pi 探测补上用户变量与任意盘用户档案（2026-11-19，未发版）

- 用户反馈：他们机器上 `pi` 直接能跑（Pi 装在 PATH 上），Core 却报「还没有配置 Pi」。
  原因有两层，都是因为 Seven Station Core 跑在 **LocalSystem** 下：
  1. `Environment.GetEnvironmentVariable("PI_AGENT_DIR")` 只拿到**机器级**变量，用户在「环境变量」
     对话框里设的是 `HKEY_USERS\<SID>\Environment`（用户级），服务账户根本看不到。
  2. 用户档案扫描写死了 `C:\Users\*`，家目录在别的盘或自定义路径的用户直接漏掉。
- 改法（`CoreSettings`）：
  - 新增 `UserEnvironmentPiDirectories()`：遍历 `HKEY_USERS\<SID>\Environment` 读 `PI_AGENT_DIR`，
    支持 `%VAR%` 展开；hive 没挂载（用户未登录）时跳过。
  - 新增 `UserHomes()`：从 `HKLM\...\ProfileList\<SID>\ProfileImagePath` 取全部用户家目录（任意盘、任意路径），
    再补 `C:\Users\*` 兜底；跳过 `systemprofile` 与 `Default`。
  - `ProfileCandidates()` 改用 `UserHomes()`。
  - 程序集级 `[assembly: SupportedOSPlatform("windows")]`（读注册表、跑服务本来就只对 Windows 成立），
    消掉 CA1416；`Microsoft.Win32.Registry` 包引用不需要，net10.0 自带。
- 实测：把 `PI_AGENT_DIR=E:\tmp\fake-pi` 写进用户级注册表（服务账户读不到的位置），
  冷启动一个临时实例，`settings.json` 的 `Agents.pi.Directory` 就是 `E:\tmp\fake-pi`——这条路径以前必然漏。
  测完已删除该注册表值、清掉临时目录。
- 出包：`dist/SevenStationCore-win-x64-0.4.1.msi` 已含本次改动（43.7 MB）。

## STATUS-030：Seven Station Control 里能改 Pi 安装目录了（2026-11-19，未发版）

- 起因：另一个用户装完之后 `list_models` 一直报「还没有配置 Pi」。Core 的自动探测只扫
  `C:\Users\*\.pi\agent\bin\pi-launcher.js`，Pi 装在别处（或没装在系统盘家目录）就找不到。
- Control 的「连接」区加了一行 **Pi 安装目录**：显示当前配置（读 `settings.json` 的 `Agents.pi.Directory`），
  不可用会显示一行提示；「更改」就地变输入框（Enter 存、Esc 取消），「重新探测」把目录清空、重启服务让 Core 重新找。
- Rust：`Snapshot` 加 `pi_directory` / `pi_ready`；`set_pi_dir()` 先校验 `bin/pi-launcher.js` 存在，
  再走提权写配置（`--do pi <path>`）；`write_pi_dir()` 改 `Agents.pi` 只动 `Directory`/`Home`，
  `Node` 等其它键保持不变，`<用户>\.pi\agent` 这种标准位置顺手推出家目录；写完重启服务。
- 校验在提权入口里也做了一遍，绕不过去。实测三种情况都对：`E:/tmp` → 退出码 4 且配置不动；
  清空 → `Directory` 变空、`Home` 保留；设好 → `Directory`/`Home` 正确、`Node` 原样保留。
- 出包：`dist/SevenStationCore-win-x64-0.4.1.msi` 已含新版 Control。界面观感需要人眼确认。

## STATUS-029：安装包的版本倒退已修（2026-11-19，未发版）

- `scripts/build-installer-center.ps1`：FileVersion 改成 `0.<年>.<月日>.<时分>`（如 `0.26.1119.2211`）——
  四段各 ≤ 65535、随构建单调递增，且一定大于历史上的 `0.4.39632.0`。不再取模，不会回绕。
- 两个 wxs 都加了 `util:CloseApplication`（挂在 `<Package>` 下，父元素只能是 Package/Module/Fragment）：
  升级前先关掉在跑的 `SevenStation.Core.exe` / `SevenStation.exe`。两个构建脚本都加了
  `-ext WixToolset.Util.wixext`，该扩展已装到 wix 的全局扩展目录。
- 已核对（由脚本重建的两个 0.4.1 MSI）：`Wix4CloseApplication` 表里分别有
  `CloseCore → SevenStation.Core.exe`、`CloseClient → SevenStation.exe`；Core 的 exe 版本
  `0.26.1119.2211`、客户端 `0.4.1.0`。服务那侧本来就有 `Stop="both" Wait="yes"`，占用这条路是通的。
- **没发版**：`<Version>` 仍是 0.4.1，没打新标签。`dist/` 里的 0.4.1 安装包已经是打了补丁的版本，
  与线上已发布的 0.4.1 资产字节不同，下次发版直接用。
- 未验证：拿 0.4.0 真装一次再升级到现在这个包（要重装旧版本，没做）。

## STATUS-028：0.4.1 升级把 Core.exe 装丢了（2026-11-19）

- 现象：用户在自己机器上装 0.4.1 的 Core MSI，安装报成功、ARP 显示 0.4.1，但
  `C:\Program Files\SevenStation Core\SevenStation.Core.exe` 不存在、`SevenStationCore` 服务也没注册；
  Seven Station Control 起成便携模式，弹「没找到 SevenStation.Core.exe」。
- 根因（用户先停了服务，排除占用）：**FileVersion 倒退了**。构建脚本用「UTC 秒数 % 60000」当第三段，
  0.4.0 出过 `0.4.39632.0`，0.4.1 回绕成 `0.4.11411.0`。两个 MSI 里 `CoreExecutable` 组件 GUID 相同
  （`{86036D2D-…}`），Windows Installer 见到目标位置是「更高版本」就跳过替换，组件却记成已安装，
  旧的卸载再把文件删掉 —— 最后 exe 与服务注册（挂在同一组件上）一起没了。
  其余文件没事：DLL 的 AssemblyVersion 是 0.4.1（更新），`wwwroot`/`json` 无版本号按内容比对。
- 处理：`msiexec /fa {976887C6-70AC-4EE9-9EA4-2174D2F41D72} /qn` 修复安装，文件与服务都回来了，
  `127.0.0.1:8787` 返回 200。
- 待修（0.4.2）：在 MSI 里用 `util:CloseApplication` 在 InstallFiles 之前关掉正在跑的
  `SevenStation.Core.exe` / `SevenStation.exe`，并给 WiX 加 `WixToolset.Util.wixext`；
  升级前先停服务这条写进发布说明的升级注意事项。
- 影响面：所有从 0.4.0 升级的机器都会碰到（服务默认常驻），不是个例。

## STATUS-027：0.4.1 发布（2026-11-19）

- 版本号三处改到 `0.4.1`（Core csproj、两个 Cargo.toml、两个 tauri.conf.json），
  发布说明 `docs/release-notes/v0.4.1.md` 过门禁（40 行、分类、链接到 `v0.4.1` 下的 STATUS）。
- 出包：`SevenStationCore-win-x64-0.4.1.msi`（43.3 MB，SHA-256 `54f96072…df7736`）与
  `SevenStation-win-x64-0.4.1.msi`（3.2 MB，SHA-256 `b6d07af3…65e53e9a`），MSI 里 ProductVersion 都是 0.4.1。
  构建信息表在 `build/release/v0.4.1.md`，源码提交 `e77e04e`。
- `main` 与标签 `v0.4.1` 用 SSH 推；Release 由用户在自己会话里执行 `gh release create` 建好。
  核对：标题 `v0.4.1`、非草稿、四个资产（两个 MSI + 两个 .sha256）无显示标签、字节数与本地一致；
  把两个包下载回来算 SHA-256，与本地逐字节一致；正文与 `build/release/v0.4.1.md` 完全相同（40 行，含构建信息表）。
- `gh` 这次踩到：新版 gh 把 token 存进 Windows 凭据管理器，`hosts.yml` 里只剩用户名，
  所以以 SYSTEM 身份（本会话）跑 `gh` 永远 invalid；发布命令要让用户在自己的会话里跑。
- 构建环境的两个坑：`dotnet publish` 卡在还原是 MSBuild server 的问题，设
  `DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1` 后 48 秒过；WiX 的 UI 扩展按系统账户档案找，
  `%USERPROFILE%` 覆盖无效，扩展要复制到 `C:\Windows\system32\config\systemprofile\.wix\extensions`。
- 没做真机安装/升级测试（生产环境隔离：不许覆盖用户正在用的 0.4.0）。

## STATUS-026：进度类操作一条通知到底（2026-11-19）

- 用户定的规则：一秒钟内完不成的操作，用**同一条**通知从「进行中（转圈）」翻成结果，
  不要开始弹一条、结束再弹一条；一次性提示（如模型回退）几秒后自己消失就行。
- 压缩：点按钮先挂一条「正在压缩上下文」+ 转圈，`compaction_end` 时同一条改成「上下文已压缩」
  （3.2 秒后消失）；Pi 自动触发的压缩没点过按钮也会补上这条。120 秒没结束就改成「压缩好像没结束」。
- 停止：`requestStop()` 挂一条「正在停止」+ 转圈，`agent_settled` 回来按 `event.aborted` 翻成
  「已停止」或「本轮已结束」；15 秒没回音提示「还没停下来，可以再点一次」。进程退出也收尾。
- 实现：`statusToast()` 返回可 `update()/remove()` 的句柄，样式 `.toast.is-busy` + `.toast-spin`。
- 动画：进场往上冒（`rise`）；退场分两段——先整块淡出下沉 150ms（文字和框一起走），
  透明后再把高度收成 0（120ms，这时看不见框，只有后面的卡片平滑上移）；
  换文案时新字淡入、卡片高度按新旧差值 200ms 过渡，变长变短都不跳。
- 未验证：动画观感（需要在界面里点一次压缩或停止看那张卡片翻面与收起）。
## STATUS-025：通知挪进正文与输入框之间的留白（2026-11-19）

- 用户定的界面规则：那块留白平时没信息，瞬时的通知就该放这儿，不要浮在右下角挡内容。
- `#toasts` 从根容器挪进 `.composer`（第一个子元素），`.toasts` 改成
  `position:absolute; bottom:100%; left/right:24px`，贴住输入区上沿、只向上叠，绝对定位所以不顶动布局。
- 样式从深色药丸改成浅色卡片（边框 + 10% 底色），warning / error 用边框颜色区分；
  `pointer-events:none`，通知不吃点击。
- 未验证：观感（需要在界面里触发一条通知看一眼）。

## 待办：批三的约定（2026-11-19 用户定）

新对话沿用上次的模型与思考等级：**全局一份**（不按工作区分）、只记**手动切换**、
继承的模型不在清单里时**弹一条简短通知**（不用详细）。还没写代码。

## STATUS-024：发送状态三态与正文里的地址路径（2026-11-19）

- **发送状态**：气泡左边一个 14px 小圈，`sending` 转、送达淡出、失败变红圆底感叹号（挖空）。
  失败时点它弹小面板：取消 / 删除这条 / 重新发送（重发沿用原文与图片，圈转回去再发一次）。
  成功后不撤气泡、不闪一下——回显那条就地换成正牌消息，只把圈淡掉。失败时气泡不再描红边（用户去掉的）。
  回显条目本来就在 `session.items` 里，`turnsOf()` 会画；**别再额外补画一遍**（踩过：气泡变两条）。
  只有从 `raw` 重建 items 的地方（`onOlderMessages`）要接回还没确认的那些。
- **地址与路径可点**：`renderMarkdown()` 外面包了 `decorateLinks()`，正文里的 `http(s)://`、
  `C:\...`、`\\server\share` 会变成链接（跳过 `a`/`code`/`pre`）。网址走新窗口打开；
  绝对路径发 WS `reveal_path`，Core 用 `explorer.exe`（目录直接开、文件用 `/select,`）打开，
  打不开回 `notice` 轻提示。
- 演示开关：`SEVENSTATION_DEBUG_FAIL_PROMPT=1` 时每句话都当场失败，用来看红圈。
- 验证：`node --check` 通过、构建 0 错误、正则单测（URL/盘符/UNC/句末标点/括号）通过；
  `reveal_path` 打不存在的路径回 `notice: warning 找不到这个路径`。用户在测试实例上确认：
  首发的气泡只有一个、红圈点的面板「重新发送」「删除这条」都符合预期（重发后转圈并送达、淡出）。
## STATUS-023：发出去立刻看得见（2026-11-19）

- 问题：说完话到界面上出现自己的气泡之间要等 3~4 秒（实测），因为气泡要等 Pi 回 `message_end`
  才画出来；同时「工作中」也要等 `agent_start`。观感上就是卡住。
- 做法：`submitPrompt()` 在发送前先 `echoPrompt()` 把这句话画进对话，同时把会话标成 streaming，
  「工作 N 秒」立刻开始走；Core 确认后（真正的用户消息到达）用 `echoAt` 把回显换成正牌，不重复。
  提示失败时 `dropEchoes()` 撤掉回显，避免留一条其实没发出去的话。
- 排队的情况不回显：那时这条还没发出去，面板才是它的位置。
- 验证：`node --check` 通过、构建 0 错误；用户在测试客户端确认「气泡立现、停止键与计秒同时切换、
  没有重复的用户消息」。
## STATUS-022：三处「界面在说谎」修掉（2026-11-19）

- 侧栏「工作中」指示器时有时无：`SendConversationList` 只在 list/new/rename/delete 时推送，
  流式开始和结束都不推，客户端也不本地改 `working`。现在状态变化统一走 `SetStreaming()`，
  它会在状态真的变化时 `BroadcastConversations()` 给所有连接。
- 新对话模型显示成 GLM-5.2：新对话 `conversations.Model` 为空，客户端 `currentModel()` 兜到
  模型列表第一条（`v2ex/coder`，显示名 GLM-5.2），而实际跑的是 Pi 的默认。现在 Core 读
  `<agent>/settings.json` 的 `defaultProvider` + `defaultModel`，用缓存列表补全字段后塞进
  `state.model`，胶囊显示的就是真正在跑的模型。
- 模型缓存永不过期：`models` / `thinkingLevels` 加上 `modelsAt` 时间戳，24 小时后自动重取；
  模型面板底部加了「刷新」，发 `list_models` 带 `force: true` 当场重取。
- 验证：临时实例（8899）跑一次真实对话，实测 `state.model = magpie/auto/deepseek-v4.1-flash`、
  `conversation_list` 在开始时 `working=true`、结束时 `working=false`、`modelsAt` 落库。
  界面观感仍需人眼确认。

## STATUS-021：对话区细节与追加输入队列（2026-11-19，开发中）

- 目标：用户提的六条界面问题——自己发的消息认不出来、左侧栏「工作中」指示器不刷新、工作中
  输入卡片被提示行顶高、输入区与正文太挤、步骤刷屏、步骤行悬停时闪、往上翻被拽回底部。
- 已落地（本地提交，未出包）：
  - 用户气泡右对齐加色，三档（跟随主题 / 微信绿 / 薄荷）并入设置页「外观」卡，默认跟随主题。
  - 输入卡片常驻灰影，聚焦时叠蓝色光环，灰影保留；提示行固定预留一行；正文底留白 44 px。
  - 工作中只留最新一条步骤，上一步一完成就折进摘要行；整轮结束后收成一行「工作 N 步」。
  - 正在跑的那一轮改为就地更新步骤文字，不再整轮重建——悬停时不再闪。
  - 滚动只在离底 24 px 内才跟随（原来 90 px），`message_start` / 用户消息落地不再强制拉到底。
  - 追加输入改由 Seven Station Core 自持队列：可看、可改单条、可删单条、可清空，
    这一轮结束后依次送出（`agent_settled` 触发）。Pi 那边的队列命令只有 `clear_queue`，单条改不了。
- 验证：`dotnet build SevenStation.slnx` 0 错误；临时 home + 8899 起核心，脚本走 WebSocket 实测
  「排队两条 → 编辑一条 → 删一条 → 清空」，并确认「本轮结束送出一条、下一轮送出下一条」，
  `session_state` 里带回队列。用户在测试客户端上验过气泡、间距、阴影、步骤收纳、面板与编辑流程。
- 测试中修掉两处：`queue_state` 被错放进 `onEvent()` 导致面板不出现；按停止时未判断
  `agent_settled.aborted`，把排队内容当成下一轮送了出去。编辑改为「取回输入框」。
- 未验证：编辑到一半该条被送出/删除时的自动退出路径（用户跳过）。界面截图仍无（SYSTEM 身份起不了无头浏览器）。
- 未做：左侧栏指示器（`SendConversationList` 只在 list/new/rename/delete 时推，流式开始结束不推）、
  新对话模型的假显示（兜到列表第一条 GLM-5.2）、模型缓存永不过期、新对话继承上次的模型与思考等级。

## 已收口：图标与安装程序画面

- 图标不再是手画：`design/make-icons.py` 用 Segoe UI Semibold 的 π 按包围盒栅格化，一次产出
  Control 的 `icon.ico`/`tray.png`、客户端的深底 `icon.ico`、网页用的 `mark-light.png`/`mark-dark.png`。
- 安装程序画面：`design/make-installer-art.py` 生成 `installer/assets/banner.bmp`（493×58）与
  `dialog.bmp`（493×312），两个 wxs 用 `WixVariable` 指过去，`Icon + ARPPRODUCTICON` 给「应用和功能」配图标。
- 真跑一次向导的截图在 `design/shots/msi-wizard-real.png`：顶栏会被 493×312 的对话框底图盖住，
  页面上看不到 banner；要留顶栏得改底图尺寸，WiX 那个控件尺寸固定，塞进去会拉伸，所以这版就是没有顶栏的样子。

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

## STATUS-002：仓库定名 SevenStation 并推到公开远端

- 按用户要求把项目搬到当前工作目录，仓库名与目录同名（均为 SevenStation），
  程序集、命名空间、exe、日志与界面品牌一并改名，避免同一产品留两个名字。
- 验证：`dotnet build` 通过，`scripts/ui-smoke.mjs` 在改名后重跑通过；`git log` 保留原有提交。
- 记录：那次 `gdi32full.dll` 崩溃（`0xc0000409`）由用户指认为本机 MacType 注入所致，
  不是代码问题，已写进 `AGENTS.md` 的已知坑。

## STATUS-003：拆成 Seven Station Core + 客户端，多端共用一份会话

- 用户 2026-11-17 定下目标形态：一套 Seven Station Core 负责跟 Pi 打交道，PC 与手机都连这套 Seven Station Core；日后客户端再加
  host 能力，用 Seven Station Core 的 Pi 操作别的设备。原「只做 PC 端」的约束作废，见 `docs/DECISIONS.md`。
- 改了什么：新增 `src/SevenStation.Core`（ASP.NET Core，管 pi 子进程、会话扫描、`/ws`、
  托管前端）；桌面端退化成 WebView2 壳，加载 Seven Station Core 地址；`AppBridge` 变成 `CenterHub`，
  命令应答回发起连接、会话事件广播给所有连接。前端从 `chrome.webview` 桥换成 WebSocket。
- 安全：Seven Station Core 默认只监听 `127.0.0.1:8787`；`/ws` 要令牌，令牌文件在 `%LOCALAPPDATA%\SevenStation.Core\token.txt`。
- 验证（2026-11-17，真窗口 + CDP + 第二个 WebSocket 客户端）：
  - `scripts/ui-smoke.mjs` 通过：连接状态「已连接」、新会话、流式、收尾、侧栏刷新。
  - 第二个客户端 `list_sessions` 拿到同一份会话（1 条），`open_session` 拿到 3 条历史。
  - 桌面窗口里发一条消息，第二个客户端收到同一条 `text_delta` 与 `agent_settled`，key 一致。
  - 同一会话打开两次只产生 1 个 key，即一个子进程，满足单写者。
- 边界：局域网与 TLS 没做；手机端只验证了同一页面能被打开（未做窄屏适配）。

## STATUS-004：Seven Station Core 定名 SevenStation.Core，与客户端分家

- 用户 2026-11-17 定名：Seven Station Core 叫 SevenStation.Core（MA 既是 Main 也是 Main Agent），客户端仍是 SevenStation。
- 改了什么：`src/SevenStation.Server` → `src/SevenStation.Core`，程序集与 exe 变成 `SevenStation.Core`；
  Seven Station Core 日志写 `%LOCALAPPDATA%\SevenStation.Core\logs\center.log`，令牌文件 `%LOCALAPPDATA%\SevenStation.Core	oken.txt`；
  环境变量改为 `SEVENSTATION_LISTEN` / `SEVENSTATION_TOKEN`；客户端从 Seven Station Core 的令牌文件自动读取。
- 界面：连接状态显示成「已连接 · SevenStation.Core」；新建会话语义改为「填 Seven Station Core 机器上的目录」；
  空状态文案不再说「本机」。
- 验证：`dotnet build SevenStation.slnx` 0 警告 0 错误；`dev-run.ps1` 起 Seven Station Core 与客户端，
  health 返回真实会话根目录，界面连上后列出 170 个会话。

## STATUS-005：对话归 SevenStation.Core，客户端只读 MA 的数据

- 用户 2026-11-17 定：不再全盘使用 Pi 的会话管理。Pi 的会话目录太乱（一百多条结束未清理），
  点开旧会话要等很久、还有加载失败的。对话改成 SevenStation.Core 自己的概念，客户端只跟 MA 通讯。
  记在 `docs/DECISIONS.md` 的 D5。
- 数据：SQLite（`%LOCALAPPDATA%\SevenStation.Core\data\sevenstation.db`，`SEVENSTATION_DATA_DIR` 可覆盖），
  两张表 `conversations` / `messages`，加一张 `settings` 放模型列表这类缓存。
- 绑定：新建对话时只建一条记录；第一次发消息才起 Pi 子进程，把 Pi 的 `sessionFile` 记进
  `pi_session_file` 字段，续聊时拿它 resume。这个字段不出现在任何客户端响应里。
- 存档：每轮 `agent_settled` 后取一次 `get_messages` / `get_session_stats` / `get_state`，
  整体替换该对话的消息并更新标题、模型、思考等级、用量。
- 验证（2026-11-17，真窗口 + CDP + SQLite 直查）：
  - 新建对话 → 发消息 → 流式 → 收尾，库里落地 5 条消息（含 1 条 system）、标题取首句、
    `pi_session_file` 已写入。
  - 关掉 Seven Station Core（子进程一起没）再开，点开同一对话：109 ms 出历史，**没有**起任何 Pi 子进程。
  - 模型列表改为读缓存后，看历史的路径完全不碰 Pi。
  - 删除对话：侧栏 2 → 1，库里对应行与消息一起消失，Pi 那边文件保留。
  - `scripts/ui-smoke.mjs` 通过（用量 `1.1% · 11k/1000k`）。
- 顺带修：`refresh_stats` 原来回的是整份状态，收尾时会用库里未写完的副本覆盖刚流式出来的内容；
  现在只回用量。SQLite 原生库有已知高危版本，已钉 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5。
- 边界：旧的 170 条 Pi 会话不迁移，MA 从空库开始；对话不能重命名（标题自动取首句）。

## STATUS-006：对话重命名，切模型不再清空线程

- 用户 2026-11-17 要：对话要能重命名，模型选择要能用（上一轮漏了）。
- 重命名：标题旁加了铅笔按钮，双击标题也能改；库里改标题，所有连着的客户端一起更新。
- 模型/思考等级：之前改设置会回整份 `session_state`，用库里还没写完的副本把流式内容覆盖掉——
  表现为「一切模型对话就空了」。改成只回 `conversation_settings`（标题、模型、思考等级），不碰消息。
- 思考等级：换过模型后库里记的等级可能不在当前模型的可选范围，界面照实把它补成一个选项，
  不再显示成空白。
- 客户端自愈：强杀后残留的 WebView2 进程占着缓存目录会报 `0x800700AA`。现在创建环境失败会重试两次，
  仍失败则提示「先关掉旧的窗口和残留的 msedgewebview2」；`dev-run.ps1` 启动前会顺手清理这些残留。
- 验证（2026-11-17，真窗口 + CDP）：切模型后消息数保持 2、线程文本不丢，下拉显示新模型；
  重开后标题、模型 `v2ex/coder-m3`、思考等级 `medium`、消息数都对；重命名后头部与侧栏同步，消息不丢。

## STATUS-007：模型下拉按 provider 分组，标签带上路由

- 用户的困惑：终端里看到的是 `magpie/auto/deepseek-v4.1-flash`，界面只显示
  `Deepseek-V4.1-Flash · routing group`，两者对不上，也不直观。
- 原因：Pi 的模型列表同时给 `provider`、`id`、`name`，界面只用了 `name`，`id` 只藏在选项值里。
- 改法：下拉按 provider 分组（`v2ex` / `explabs` / `magpie` / `oneapi`）；标签是 `name`，
  若 `id` 带路由前缀而名字里没体现，再补 ` · 路由`（如 `· auto`）；悬停提示给完整 `provider/id`。
  分组标签本身就对应终端里那串前缀。
- 验证：17 个模型分 4 组；当前项显示 `Deepseek-V4.1-Flash · routing group · auto`，
  title 为 `magpie/auto/deepseek-v4.1-flash`。

## STATUS-008：按 pi-gui 重做布局

- 用户指出布局离 pi-gui 差太远，给了截图参照。改成它那套浅色形态，不再是深色两栏。
- 外壳：页面灰底 + 圆角窗口，侧栏独立成浅灰面板。
- 侧栏：顶部收起按钮、整行「新建对话」、导航（对话带数量 / 设置）、对话段头带筛选按钮；
  列表按日期分组（今天 / 昨天 / 本周 / 更早），每条两行：标题 + 时间，工作区 + 运行状态。
- 主区：面包屑「工作区 / 标题」+ 重命名铅笔；空态是品牌方块 + 小标签 + 大标题 + 工作区选择。
- 输入区：卡片 + 胶囊行（工作区、模型、思考等级）+ 圆形发送键；提示行只在有队列时出现。
  空态直接输入也会建对话再发出去。
- 其他：设置面板显示 Seven Station Core 地址、版本、对话库路径、连接状态；侧栏可收起（状态存本地）。
- 验证：真窗口截图（空态、对话、折叠、设置）；新建 → 选 auto 模型 → 发一句话 → 收到「收到」；
  标题在发送后即时更新到面包屑与侧栏。
- 与参考的差异：参考里的 Scheduled / Skills / Extensions 三个入口我们没有对应功能，没有摆空壳。

## STATUS-009：输入侧与常驻

这轮补的都是"用起来缺的那几块"，UI 细化留到后面。

- `@` 文件补全：Seven Station Core 自己扫工作区（不经过 Pi），选了目录会接着往下钻；只允许在工作区里找。
- `/` 命令列表：走 Pi 的 `get_commands`（扩展命令、提示词模板、技能），按工作区缓存。
- 图片可以粘贴、也能直接拖进窗口；拖的时候编辑区给个高亮。
- 代码块右上角「复制」，悬停才出现。
- 头部「导出为 Markdown」：标题、工作区、时间，用户与助手正文，工具调用一行一条。
- Seven Station Core 常驻：`scripts/start-center.ps1`（隐藏窗口、等端口真起来）、`stop-center.ps1`、
  `install-autostart.ps1`（登录自启的计划任务，支持 `-Status` / `-Remove`）。
- 内容根固定到 exe 目录：从任何位置启动都能找到 `wwwroot`，打包后双击也不会白屏。

## STATUS-010：终端面板

- 每条对话一个常驻 shell，从头部终端图标打开，面板摆在对话区与输入区之间。
- 实现走 `pwsh -Command -`：命令一行一行喂标准输入，变量与工作目录都延续，
  不需要 PTY，也不用在前端塞终端模拟器。代价写在文档里：交互式全屏程序（vim、top）用不了。
- 没有 pwsh 的机器退回系统自带 powershell；非 Windows 平台走 `bash -s`。
- 输出是广播：两台设备同时开着，看到的是同一条命令流的同一份输出；
  后来打开的设备会把已经跑出来的缓冲补上。
- 面板上有清空、结束进程、收起；输入框支持上下键翻历史，Esc 收起。
- 控制序列在服务端就剥掉了，界面只显示纯文本，不会变成乱码。

## STATUS-011：工作区文件浏览

- 头部文件夹图标打开右侧抽屉：面包屑路径、进入目录、上一层、关闭。
- 文件点开是只读预览，等宽字体；超过 512 KB 或看起来是二进制的文件直接说明原因，不去猜。
- 路径只能在会话工作区里走：`..` 或绝对路径跑出去一律当作找不到。
- 抽屉打开时正文（顶栏、对话区、终端、输入区）整体让开，不压在输入框上面。

## STATUS-012：搜索、记忆与几个日常缺口

- 搜索：标题与正文一起搜，结果里带命中的那一小段。正文存在 `messages.text` 列
  （`payload` 里的中文是 `\uXXXX` 转义的，直接 LIKE 搜不到），老库启动时补列并回填。
- 工作区记忆：用过的目录记在 Seven Station Core（最多 8 条），新建对话时直接挑；输入框会实时检查目录在不在，
  不存在就不让点「开始」。
- 快捷键：Ctrl+K 新建、Ctrl+B 侧栏、Ctrl+J 终端、Ctrl+E 文件、Ctrl+F 搜索、Esc 关弹层。
- 设置面板：Pi 装没装、会话目录、最近工作区、快捷键清单，外加「看最近日志」——
  手机上出问题先看这一屏。
- 断线兜底：Seven Station Core 没起来时，客户端不再是一页白屏，而是说明连的哪个地址、配置在哪、5 秒自动重试。
- 工具卡片里的文件路径点一下就打开文件面板；聊天里的图片点开可放大。
- 切回窗口时自动对一次会话列表与当前状态（别的设备可能改过）。

## STATUS-013：窗口记忆与图片压缩

- 客户端记住窗口大小与位置（`%LOCALAPPDATA%\SevenStation\window.json`）；
  换了显示器导致坐标跑出屏幕时只恢复尺寸，位置仍回到居中。
- 粘进来或拖进来的图片超过 1600 像素就先等比缩小再发（4000×2000 的截图从 157 KB 降到 39 KB）；
  透明图仍存 PNG，GIF 不动。

## STATUS-014：小收尾

- 设置里显示 Seven Station Core 对外地址，并标出「只本机能连」还是「局域网可连」。
- 对话滚动往上翻时出现「回到最新」，新内容不会把位置顶走。

## STATUS-015：冒烟脚本跟上界面

- `scripts/ui-smoke.mjs` 之前靠按钮文案判断「正在跑」，改成看 `stop` 类；
  新建会话后也不再等「线程出现」，而是等「换成新会话」——旧会话开着时线程本来就可见。
- 工作区输入框不再禁用「开始」：目录检查只给提示，真正把关的是 Seven Station Core 建对话时的校验。
  禁用来禁去会跟人抢跑道，也让自动化脚本点不动。

## STATUS-016：按原型做掉六处细节

- 侧栏的技能与扩展数量改成连上就拉一次，不用点进页面才出现。
- 对话行的删除键换成 24 像素圆角图标键，跟「几分钟前」占同一个位置：悬浮时时间淡出。
  行本身从 `<button>` 换成 `role="button"` 的 div，删除键才能落进第二行。
- 工作区胶囊的箭头从字体符号改成 12 像素图标，名字过长中间省略，箭头固定在右端不参与挤位。
- 模型面板高度写死 392 × 420：切分组、搜模型、只剩三个模型都不再跳高跳矮（原先是 403 上下浮动）。
- 图标按钮尺寸归到 26 / 22 / 28 / 32 / 24 五档，图标按边长取整像素；发送与附加键换成图标。
- 小圆点归到 8 / 12 / 16 三档。原来是 7 和 11，1.25 倍缩放下落在半个物理像素上，
  截图放大是 8×9 的带平边糊块，看着不像圆。
- 顺带修三处暗色主题下看不见的图案：停止方块与开关旋钮写死 `#fff` 压在近白底色上，改成 `--on-accent`；
  侧栏折叠键的悬浮底色也改成主题色。

## STATUS-017：状态点、进行中的历史、每步与整轮用时

- 侧栏状态点按状态上色：跑着是转圈，跑完是绿色，只有还没开始过的空对话是灰色。
  之前跑完也是灰色，看着像出了问题。
- 这一轮还在跑的时候就把内容落库：用户的话、每段模型输出、每个工具结果一产生就
  `AppendMessages` 追加，轮次结束再由 `PersistTurnAsync` 拿 Pi 的完整消息整体替换（能自愈）。
  中途切到别的对话再切回来，看到的是正在跑的内容，不再是空白。
- 每步耗时一直在 Pi 的消息里（`toolResult.durationMs`），历史渲染没往界面传，所以完成之后只剩步数；
  现在补上。整轮用时按消息自己的时间点算（用户那句话到最后一次动作），不再把各步相加当总计；
  还在跑的时候按开始到现在每秒刷新，和输入框下面的提示是同一个数。

## STATUS-018：发布口径收口（命名、许可证、死配置）

- 产物改名成 `<项目名>-<平台>-<架构>-<版本>.msi`：Seven Station Core `SevenStationCore-win-x64-<版本>.msi`，
  客户端 `SevenStation-win-x64-<版本>.msi`。两个 Release 分开建，名字与包名一致。
- 仓库加 `LICENSE`（GPLv3 全文），README 增「装起来用」「许可证」两节，「发布」一节按 MSI 重写；
  `RELEASE.md` 按两个产品的命名、门禁、`gh release create` 两条命令重写。
- 两个安装程序的许可页都换掉 WiX 自带的假文字，改用 `installer/license.rtf`（GPLv3 说明与安装会做的事），
  构建脚本用 `-d LicenseRtf=` 传进去，不再各自放一份。
- 删掉客户端 MSI 里只写不读的 `HKLM\SOFTWARE\SevenStation`（`CenterUrl`/`CenterToken`）：
  安装向导早就不问这两项了，装一次写一对空字符串，属于死配置。客户端现在只认
  `%LOCALAPPDATA%\SevenStation\center.json`、`token.txt` 与环境变量，`winreg` 依赖一并去掉。
- 修「Pi 名片里的会话目录」：写在 `~/.pi/sessions`，那里根本不存在，Pi 1.5 的会话在
  `<agent 目录>\sessions`。
- 发布说明拆成 `docs/release-notes/v0.3.0-core.md` 与 `v0.3.0-client.md`，两份都过门禁
  （`--link-pattern '/blob/[^/\s]+/'`，因为标签不带 `v` 前缀）。
- 验证：`dotnet build SevenStation.slnx` 0 错误；两个 MSI 用 `wix msi decompile` 核过
  （客户端无 Registry 表、Seven Station Core 有 `ServiceInstall`、两边许可页都是 GPL 文本）；
  两个包的 `.sha256` 与文件对应。

## STATUS-019：从 Pi / magpie 恢复会话

- 入口：侧栏「新建对话」右侧常驻一个恢复按钮（原型 A 版）。没配过令牌的新机器仍走引导页。
- 对话框（原型 P1 版）：左列表右预览。列表按工作区分组、按开始时间倒序，能搜标题或工作区；
  每行给标题、时间，已收进对话库的变灰并标「已恢复」。预览给首句、消息数、大小、时间、模型、
  工作目录和最近一条回复。
- 两栏的来源：`Pi` 直接扫 `<agent 目录>\sessions`；`magpie 命令`粘一整条
  `Set-Location -LiteralPath '…'; pi --session <id>`，Seven Station Core 解析出会话 id 再匹配，那一栏是单栏。
- 恢复的语义是**接管**不是复制：对话绑定到原来那个 Pi 会话文件（`pi_session_file`），
  消息按 `user`/`assistant`/`toolResult` 搬进对话库用于渲染，续聊时 `pi --session <文件>`
  接着原来那段跑。验证过：恢复 389 条那段，发一句「只回复两个字：继续」，原 jsonl 从 437 行涨到 438 行。
- 重复恢复直接打开已有那段（`already: true`），不会复制出第二份。
- 扫描带缓存（路径 + mtime + 大小）：197 段冷扫 236 ms，热扫 24 ms。
- 顺手加的：`.modal textarea { min-height: 140px }` 会压住更具体度更低的规则，
  命令输入框带上 `.modal` 前缀才定得住 54 px。

## 归档索引
