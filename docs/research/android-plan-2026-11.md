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

## 三、已经定下来的四件事（2026-11-20 用户确认）

1. **连接方式**：手动填地址 + 扫码，两种都做；mDNS 自动发现不做。
2. **授权走扫码配对**：Control 里点「添加设备」，屏幕上出二维码，手机扫一下就连上并拿到自己的令牌。
3. **系统通知先不做**：推送通道的方案以后再想，先不占前台服务。
4. **只在家里局域网用**：后面走 EasyTier / ZeroTier 时，对 Core 来说只是多监听一个虚拟网卡地址，不用改别的东西。

## 三之二、扫码配对的实现方案

要点：**手机拿到的不是主令牌**，而是一次配对换来的独立令牌。这样一台手机丢了可以单独作废，桌面客户端不受影响。

### Core 侧

- 新表 `devices`：`id`、`name`（手机型号）、`token`、`created_at`、`last_seen_at`、`revoked_at`。
- 鉴权改成「接受主令牌（`token.txt`）或任意未作废的设备令牌」，`/ws` 与配对接口之外的行为不变。
- 三个接口（都需要主令牌或已配对设备，除 `redeem` 外）：
  - `POST /api/pair/new` → `{ code, expiresAt }`，码 5 分钟有效、一次性、用完即废；
  - `GET /api/pair/qr?code=...` → 直接回 **PNG 二维码**（Core 自己画，Control 不用引二维码库）；
  - `POST /api/pair/redeem` `{ code, name }` → `{ token, deviceId }`，这个接口不要求令牌，但要限速（同一 IP 每分钟几次）。
- 设备管理：`GET /api/devices`、`POST /api/devices/{id}/revoke`。
- 二维码内容用自定义 scheme，手机相机和应用内的扫码器都能认：
  `sevenstation://pair?v=1&url=http%3A%2F%2F192.168.1.5%3A8787&code=K7Q2-9F3M`

### Control 侧

- 「添加设备」按钮 → 弹窗：列出本机的私网地址（`192.168.*`、`10.*`、`172.16~31.*`）让用户挑，选完显示二维码 + 那段文字码（手输也有路）。
- 弹窗里顺带列出已配对设备（名字、最后在线时间、作废按钮）。
- 核心考量：Control 已经能用主令牌跟 Core 说话，这几个接口直接复用现有那条通道。

### Android 侧

- 扫码器（ML Kit 条码扫描或 zxing-embedded，二选一，阶段 0 定），扫到 `sevenstation://pair` 就 POST `redeem`，
  把 `{url, token}` 存进 DataStore，然后 WebView 打开 `url/?token=...`。
- 也保留手输：地址 + 令牌两个框，给「Control 没开着」的场合兜底。

### 需要顺手处理的老问题

- 静态页面现在**不校验令牌**（只有 `/ws` 校验）。绑定局域网之后，同网段的人能打开页面但连不上 WS，数据不会漏；
  要更稳就把静态文件也挂到令牌校验后面，这一步放进阶段 3。
- 配对码是唯一的弱口令，所以：5 分钟过期、一次性、限速、失败次数上限，日志里不记完整码。

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

## 六、落地顺序（按依赖排，不按 App 排）

1. **Core 先做配对**：`devices` 表 + 三个接口 + 二维码 PNG。做完用 curl 就能验，不依赖手机。
2. **Control 做「添加设备」**：地址选择 + 二维码 + 已配对设备列表。做完就能拿另一台电脑或手机浏览器试配对。
3. **Android 阶段 0**：Gradle 工程 + 扫码/手输 + WebView 加载 Core，在 Pixel 上验收。
4. 之后按阶段 1 / 2 推进。

这样每一步都能单独验证，不会卡在「整个 App 没跑起来」。

工程位置：`src/SevenStation.Android/`，与 Core、Desktop 同级；版本号跟 `Cargo.toml` / `csproj` 对齐。
