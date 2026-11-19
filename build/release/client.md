客户端的这一版是收尾：安装程序换了自己的画面，产品名统一成 Seven Station。只发 Windows x64 包。

## 新功能

- **安装向导有自己的画面**：顶栏、侧图、图标都是 Seven Station 自己的，「应用和功能」里也是同一枚图标。
- **产品名统一**：安装程序与快捷方式都叫 Seven Station，面向用户的文字里不再出现「客户端」这类译名。

## 问题修复

- 同版本重装时 Windows Installer 会跳过替换文件，这回的安装包每次构建都带新的文件版本号，覆盖装必定换到新文件。

连接信息还在 `%LOCALAPPDATA%\SevenStation\center.json`，覆盖装不用重新填地址和令牌。

完整实现与验收记录见 [进度记录](https://github.com/huaxianyan/SevenStation/blob/SevenStation-win-x64-0.4.0/docs/STATUS.md)。

本项目采用 GNU 通用公共许可证第 3 版发布，许可证全文见 [LICENSE](https://github.com/huaxianyan/SevenStation/blob/SevenStation-win-x64-0.4.0/LICENSE)。

## 构建信息

| 项目 | 值 |
| --- | --- |
| 版本 | `0.4.0` |
| 平台 | `Windows` |
| 架构 | `x64` |
| 运行时 | WebView2（Win11 自带，Win10 多数已装） |
| 安装形态 | MSI，开始菜单与桌面快捷方式 |
| 连接信息 | `%LOCALAPPDATA%\SevenStation\center.json`（覆盖装保留） |
| 源码提交 | `8ae247c0b7dc9d98e9ca3f72b2fc78fac4de6664` |
| 产物 | `SevenStation-win-x64-0.4.0.msi`（3.2 MB） |
| SHA-256 | `ce08ca64ed796f9e0afcc95c4e2196d9c02a67a7d1f12fcbca75cea84e111f51` |
