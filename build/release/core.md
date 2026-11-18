七站的中心：把 Pi 挪到一台常驻的机器上跑，会话、工作区、子进程都在那边，PC 和手机连过来看到的是同一份。
这是第一个发布版本，只发 Windows x64 包。

## 新功能

- **常驻服务**：装成 Windows 服务 `SevenStationCore`，跑在系统账户下，开机自动启动；默认只监听 `127.0.0.1:8787`，访问要带令牌，令牌在 `%ProgramData%\SevenStation.Core\token.txt`。
- **对话库**：会话按工作目录分组、按日期排列；标题在第一句发出后自动生成；改名与删除只动中心的记录，不碰 Pi 自己的会话文件。
- **一处会话多处看**：同一个会话被多个客户端打开时共用一个 Pi 子进程，正文、思考、工具调用广播给所有连接。
- **设置页**：外观（明暗与四套配色）、中心地址与版本、Pi 的版本与目录、最近工作区、快捷键、自定义样式、运行日志，都在这一页改，改完所有设备一起生效。
- **技能与扩展**：把 Pi 那边的技能清单、扩展包与扩展命令读出来摆在界面上，装没装、什么版本一目了然。
- **配置自愈**：服务第一次启动自己找 Pi（`PI_AGENT_DIR` 优先，其次各用户目录下的 `~/.pi/agent`），找不到也能在设置里补；旧配置缺家目录会自己补上。

中心以后会出 Linux 包，这版只提供 Windows x64。

完整实现与验收记录见 [进度记录](https://github.com/huaxianyan/SevenStation/blob/SevenStationCore-win-x64-0.3.0/docs/STATUS.md)。

本项目采用 GNU 通用公共许可证第 3 版发布，许可证全文见 [LICENSE](https://github.com/huaxianyan/SevenStation/blob/SevenStationCore-win-x64-0.3.0/LICENSE)。

## 构建信息

| 项目 | 值 |
| --- | --- |
| 版本 | `0.3.0` |
| 平台 | `Windows` |
| 架构 | `x64` |
| 运行时 | `.NET 10`（自包含，目标机器不用装 .NET） |
| 安装形态 | MSI，注册服务 `SevenStationCore`（系统账户、开机自启） |
| 监听地址 | `http://127.0.0.1:8787`，令牌在 `%ProgramData%\SevenStation.Core\token.txt` |
| 数据目录 | `%ProgramData%\SevenStation.Core`（卸载保留） |
| 源码提交 | `dcbd9532d7c14daa68dc158059e227b97d45b1ad` |
| 产物 | `SevenStationCore-win-x64-0.3.0.msi` |
| SHA-256 | `1c35ac321790399f2024d47de2d710901f6c2c7e1c1926feb5f182ea9e6791d9` |
