# 长期决策

只记被确立或被推翻过的方向，改了才更新。日常进度在 `docs/STATUS.md`。

## D1 中心 + 客户端，替代「只做 PC 端」

2026-11-17 用户定。原约束是「只要 PC 端，一个独立桌面应用；不做网页版，不做手机端」——
这条作废，出处是用户当天说明：中心化部署一套系统负责与 Pi 交互，客户端只连这套中心，
在 PC 或手机上打开对接的都是同一份会话；以后客户端再加 host 能力，用中心的 Pi 去操作别的设备。

交互形态参考 alu-dock，但只借使用体验，不照搬实现：alu-dock 依赖 `aruru-project/pi` 的 fork，
本项目坚持用本机已装的上游 `pi`。

## D2 只有中心起 Pi 子进程

会话、`pi --mode rpc` 子进程、会话文件扫描全在中心。客户端不碰 Pi，也不读会话文件。
结果：换设备只是换个客户端，任务仍在中心那台机器上跑。

同一个会话被多个客户端打开时复用同一个子进程，只有一个写者。

## D3 页面由中心托管，桌面壳与浏览器共用一份前端

前端只有 `src/MAStation/wwwroot` 一份。桌面客户端是 WebView2 壳，加载中心地址；
手机浏览器打开同一地址即可。命令与事件都走 `/ws?token=`，不再依赖 `chrome.webview` 桥，
所以同一套 `app.js` 在两种环境里行为一致。

## D4 命名：中心 MAStation，客户端 PiAgentStation

2026-11-17 用户定。MA 既是 Main 也是 Main Agent 的缩写，中心就叫 MAStation；面向用户打开的那个
窗口叫 PiAgentStation。两个名字分工固定：说 MAStation 指部署在中心那套服务，说 PiAgentStation
指客户端。程序集、日志目录、令牌位置都按这个分：中心写 `%LOCALAPPDATA%\MAStation`，
客户端写 `%LOCALAPPDATA%\PiAgentStation`。
