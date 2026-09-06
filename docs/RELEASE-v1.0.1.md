# 栖格 DeskNest v1.0.1

本次更新聚焦布局保存可靠性、桌面分区交互与 UI 细节。

## 下载哪个文件？

| 文件 | 用途 |
| --- | --- |
| `DeskNest-Setup.exe` | 推荐：用户级安装向导，创建快捷方式，支持系统卸载 |
| `DeskNest-MVP-win-x64.zip` | 便携版：完整解压运行，也附带 `安装栖格.cmd` |
| `SHA256SUMS.txt` | 用于校验上述两个安装文件的 SHA-256 |

**运行要求：Windows 10 / 11 x64 + Microsoft .NET 8 Desktop Runtime x64。**

运行时不包含在安装包内，请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 **Desktop Runtime x64**。仅有普通 .NET Runtime 或 .NET 9 / 10 不满足要求。

## 本次更新

- 修复布局并发保存、损坏主文件覆盖健康备份、空记录及重复 ID 等问题。
- 修复桌面监听退出竞争、列表裁切、文件夹拖入高亮造成的内容抖动。
- 改善浅色主题、主按钮悬停对比度、键盘焦点和空分区提示。
- 新增柔和悬停过渡，扩大细滚动条操作区域。
- 核心测试 53/53 通过，WPF 控件冒烟测试通过，Release 发布和 Setup 编译成功。

## 更新与安全说明

- 更新前建议通过托盘菜单退出旧版；覆盖安装不主动清空本地布局。重要布局可先备份 `%LOCALAPPDATA%\DeskNest`。
- 安装包未签名，Windows 可能显示发布者或 SmartScreen 提示。请核对下载来源与校验值，不要关闭系统安全防护。
- PowerShell 校验示例：`Get-FileHash .\DeskNest-Setup.exe -Algorithm SHA256`，将结果与 `SHA256SUMS.txt` 中的对应行比较。
- “移出分区”只移除映射；“删除”“重命名”“清空回收站”等会操作真实文件。
- 多显示器、混合 DPI、Explorer 重启及安装 / 卸载全流程仍需更多实机验证。

完整说明：[使用指南](https://github.com/13075061852/desktop-ui/blob/main/docs/USER-GUIDE.md) · [更新日志](https://github.com/13075061852/desktop-ui/blob/main/CHANGELOG.md)
