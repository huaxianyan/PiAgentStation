Seven Station Core 的这一版把控制台也装进来了：装一次，服务和能管它的窗口一起到位。只发 Windows x64 包。

## 新功能

- **Seven Station Control 随包发布**：开停服务、看运行日志、改监听地址、查看访问地址与令牌，都在一个窗口里。关掉窗口只是收进托盘，服务照常跑；退出走托盘右键。
- **局域网连接开关**：打开时按提示放行防火墙，只对本地子网开放；访问地址显示真实内网 IP，不再是 `0.0.0.0`。
- **安装向导有自己的画面**：顶栏、侧图、图标都是 Seven Station 自己的，「应用和功能」和开始菜单里也是同一枚图标；完成页可以直接打开 Seven Station Control。
- **网页版空状态**换成同一枚图标，favicon 跟着换。
- **命名统一**：面向用户的文字只写 Seven Station Core 与 Seven Station Control。

## 问题修复

- 同版本重装时 Windows Installer 会跳过替换文件，这回的安装包每次构建都带新的文件版本号，覆盖装必定换到新文件。

从 0.3.0 升上来不用先卸载：数据在 `%ProgramData%\SevenStation.Core`，配置、令牌、对话库都保留。

完整实现与验收记录见 [进度记录](https://github.com/huaxianyan/SevenStation/blob/SevenStationCore-win-x64-0.4.0/docs/STATUS.md)。

本项目采用 GNU 通用公共许可证第 3 版发布，许可证全文见 [LICENSE](https://github.com/huaxianyan/SevenStation/blob/SevenStationCore-win-x64-0.4.0/LICENSE)。

## 构建信息

| 项目 | 值 |
| --- | --- |
| 版本 | `0.4.0` |
| 平台 | `Windows` |
| 架构 | `x64` |
| 运行时 | `.NET 10`（自包含，目标机器不用装 .NET） |
| 安装形态 | MSI，注册服务 `SevenStationCore`（系统账户、开机自启），带 Seven Station Control |
| 监听地址 | `http://127.0.0.1:8787`，令牌在 `%ProgramData%\SevenStation.Core	oken.txt` |
| 数据目录 | `%ProgramData%\SevenStation.Core`（卸载保留） |
| 源码提交 | `86aa138dff9d968621cdb694aa04a10c480fa854` |
| 产物 | `SevenStationCore-win-x64-0.4.0.msi`（43.3 MB） |
| SHA-256 | `50ea243b1bb202094f530717f7ecb585a79cabefec2380d17deb3d7c223d41b1` |
