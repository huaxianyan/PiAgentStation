# Android 版规划（2026-11-20 起）

目标：手机上能接着用同一个 Seven Station Core —— 看对话、发消息、看步骤、翻文件。定位是**第三个薄客户端**，
不是第二个产品。Core 仍然是唯一的大脑（Pi 子进程、对话库、WebSocket、页面都在它那里）。

## 一、三条路，先选一条

| 路线 | 做法 | 代价 | 结论 |
| --- | --- | --- | --- |
| **A. WebView 薄壳** | Kotlin + Compose 外壳，WebView 加载 Core 的同一个页面，补原生能力（返回、安全区、文件选择、外链、常亮） | 最小；前端一处改动两边生效 | **推荐** |
| B. Tauri 2 移动端 | 复用桌面 Rust 壳 | Android 要 NDK + cargo + Gradle 三套工具链，构建链最脆 | 先不碰 |
| C. 原生 Compose 客户端 | 重写界面 + 一份 Kotlin 的 WS 协议客户端 | 工作量等于把前端重做一遍，两套界面必然漂移 | 只有「必须原生体验」时才做 |

选 A 的理由：桌面已经是「薄客户端 + Core 页面」这个形态，手机沿用同一条思路，差异压到最小；
`wwwroot` 里已有的移动端尺寸样式（输入卡片、侧栏折叠）能直接吃。

## 二、分期

### 阶段 0 · 能跑起来（1~2 天）

- Gradle 工程（Kotlin DSL、Compose 外壳），`applicationId` 先用 `com.huaxianyan.sevenstation`。
- 连接页：填地址 + 令牌（`http://192.168.x.x:8787` + `token.txt` 内容），存进 DataStore；支持粘贴整条带 `?token=` 的地址。
- WebView 加载 Core，处理：edge-to-edge + insets、返回键先走网页历史、深色跟随系统、JS 与 DOM 存储开启、外链交给系统浏览器。
- 明文 HTTP：只在「私网地址」放行（`network_security_config` 按网段白名单），公网地址强制 https。
- 验收：同一台手机连上本机 Core，能看对话、发一条消息、收完一轮。

### 阶段 1 · 真的能用（3~5 天）

- 断线与重连：Core 重启后页面自己重连（前端已有退避重连），壳在断线时给一条系统级的提示条。
- 软键盘：`adjustResize` + insets，输入时线程不被遮；发出去后自动收键盘。
- 图片附件：WebView 的 file chooser 接到系统选择器（相册 + 拍照）。
- 长按选中、代码块一键复制（`ClipboardManager`）、正文里的地址走 Custom Tabs。
- 文件面板：手机上默认收成抽屉，点路径才展开。
- 验收：真机（Pixel 10 Pro）上完整走一遍发消息、看步骤、翻文件、复制、外链。

### 阶段 2 · 手机才有的东西（3~5 天）

- 横屏 / 平板双栏（`WindowSizeClass`，`PaneScaffold`）。
- 常亮 / 防息屏开关（跑长回合时不掉线）；可选前台服务保连接（只在用户开启时）。
- 动态取色（Material You）只给外壳（连接页、抽屉、工具条），页面本身仍是 Core 的主题。
- 分享：把一段对话分享成图片或文本。
- 验收：横竖屏切换不丢状态、切后台再回来连接还在。

### 阶段 3 · 安全与发布

- 安全边界先写清楚：**Core 绑定局域网就等于把这台机器的 Pi shell 开放出去**。
  手机端必须：令牌只存本机、不许外发；只允许私网地址；日志里不打印令牌；可选的「只在指定 Wi-Fi 下连接」。
- 发布：签名材料放 `%USERPROFILE%\.android\signing\SevenStation`，APK 命名与版本号跟 Core / 桌面一致。
- 自动更新先不做（没有分发渠道就手动装）。

## 三、要你拍板的四件事

1. **连接方式**：手动填地址+令牌 / Control 显示二维码扫一扫 / 局域网自动发现（mDNS）。建议先做手动 + 二维码，自动发现以后再说。
2. **令牌怎么送到手机**：照抄一串字符，还是 Control 里点「添加设备」生成一次性码？
3. **要不要系统通知**（回合跑完提醒你）。没有公网推送通道，只能前台服务常驻，费电；默认不做，做成开关。
4. **Core 对外绑定**：手机要连，Core 就得监听局域网地址。是只在家里 Wi-Fi 用，还是要走 EasyTier / Tailscale 这类组网（本机已有 EasyTier 的痕迹）。

## 四、技术基线（2026-11-20 核对本机）

- Android SDK：Scoop 的 `android-clt`，`platforms/android-36`、`build-tools/36.0.0`、`platform-tools` 都在。
- 主力设备 Pixel 10 Pro 跑 Android 16 → `compileSdk 36`、`targetSdk 36`、`minSdk 26`。
- JDK 17（机器级），Gradle 用 wrapper；不在项目目录里放 JDK / SDK。
- 界面走原生 Material 3：edge-to-edge、预测性返回（`OnBackPressedCallback` 或 `PredictiveBackHandler`）、动态取色；
  不用实验性 Expressive API 当基线。
- 设备调试：ADB 语义按机器约定（本机 adb / 远程 `tcp:10.77.7.77:15037`）；临时产物用完删。

## 五、已知风险

- **WebView 与 Core 页面的耦合**：页面里已有的 `window.__TAURI__` 分支在手机上不存在，`reveal_path` / `open_url` 这些要走 Android 的注入接口，得加一层 `window.__SS_ANDROID__` 桥。
- **target=_blank**：桌面壳踩过一次（Tauri 不接 `NewWindowRequested`），WebView 里要显式 `setSupportMultipleWindows(false)` 并接管 `shouldOverrideUrlLoading`。
- **Core 断线后的状态**：手机上切后台，WebView 可能被冻结，回来要能自愈（前端已有重连 + 重拉 `open_conversation`）。
- **明文 HTTP 的信任边界**：内网也必须带令牌，`network_security_config` 只对私网段放开。
- **没有推送通道**：通知只能是前台服务，跟省电策略打架，先做成显式开关。

## 六、落地顺序建议

1. 先在本机把一个最小壳跑通（阶段 0 全部），验证 WebView 加载 Core 页面有没有拦路的问题。
2. 再决定连接方式与二维码（要动 Control 的界面）。
3. 后面按阶段 1 / 2 推进，每阶段在 Pixel 上验收一轮。

工程位置：`src/SevenStation.Android/`，与 Core、Desktop 同级；版本号跟 `Cargo.toml` / `csproj` 对齐。
