<div align="center">

<img src="assets/logo.svg" width="88" alt="栖格 DeskNest 标志" />

# 栖格 · DeskNest

**让桌面各归其位，让常用文件触手可及。**

一款面向 Windows 10 / 11 的轻量桌面分区管理工具，支持图标分类、拖拽整理、布局联动与个性化外观。

[下载最新版](https://github.com/13075061852/desktop-ui/releases/latest) · [使用指南](docs/USER-GUIDE.md) · [更新日志](CHANGELOG.md) · [反馈问题](https://github.com/13075061852/desktop-ui/issues)

</div>

## 为什么选择栖格？

| 功能 | 使用体验 |
| --- | --- |
| 一键分类 | 按应用、文档、图片、文件夹及其他类型整理桌面项目 |
| 拖拽管理 | 支持分区间移动映射、同区排序、从资源管理器拖入 |
| 灵活布局 | 拖动、缩放、折叠、锁定；相邻分区吸附与布局联动 |
| 两种视图 | 图标视图适合快捷启动，列表视图适合浏览文件名 |
| 个性外观 | 浅色 / 深色 / 跟随系统、透明度、图标大小与壁纸设置 |
| 常驻桌面 | 托盘运行、可配置开机启动、可切换原生桌面图标显示 |
| 本地保存 | 布局保存在本机，支持备份恢复，无需注册账号 |

### 整理映射，不等于移动文件

“一键整理”、分区间移动、“移出分区”“清空映射”和“删除分区”只改变映射，不会删除真实文件。

**注意：**菜单中的“重命名”“删除（移入回收站）”“清空回收站”，以及将文件拖入真实文件夹，是实际文件操作。请区分“移出分区”和“删除”。

## 下载与安装

### 系统要求

- Windows 10 / 11，x64 环境。
- [Microsoft .NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)。请安装 **Desktop Runtime**，而不是仅安装普通 .NET Runtime；仅有 .NET 9 / 10 不能替代本项目要求的 .NET 8。
- 当前发布为框架依赖版本，运行时不包含在安装包中。

### 推荐：安装版

1. 前往 [Releases 下载页](https://github.com/13075061852/desktop-ui/releases/latest)。
2. 下载 `DeskNest-Setup.exe` 并运行，按向导完成用户级安装。
3. 从桌面或开始菜单启动“栖格 DeskNest”。

安装到 `%LOCALAPPDATA%\Programs\DeskNest`，无需管理员权限。更新前建议通过系统托盘退出旧版；安装程序会处理仍在运行的旧进程。

### 便携版 / 脚本安装

下载 `DeskNest-MVP-win-x64.zip`（兼容现有发布命名），**完整解压**后运行 `DeskNest.App.exe`；也可以双击 `安装栖格.cmd` 创建用户级安装和快捷方式。

> 不要直接在 ZIP 预览窗口里启动程序。安装包未做代码签名，Windows 可能显示发布者或 SmartScreen 提示；请核对仓库来源及 Release 中的 SHA-256 校验值，不要关闭系统安全防护。

## 三步开始使用

1. **一键整理**：将桌面项目映射到不同分区。
2. **调整布局**：拖动分区标题、调整边缘尺寸，双击标题改名；设置好后可锁定布局。
3. **日常使用**：双击图标打开文件，右键查看更多操作；退出请使用托盘菜单。

更多操作、配置备份和卸载方式见 [使用指南](docs/USER-GUIDE.md)。

## 本地构建

技术栈：**C# / .NET 8 / WPF**。应用代码不依赖第三方 NuGet 包。

准备 Windows、.NET 8 SDK；生成 Setup 还需安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)。脚本优先使用仓库本地 `.tools\dotnet\dotnet.exe`，否则使用 PATH 中的 `dotnet`。

```powershell
# 无需 Node.js：还原、测试、发布、生成 ZIP 和可用的 Setup
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1

# 若已安装 Node.js，也可使用等价入口
npm run build
```

单独运行验证：

```powershell
dotnet run --project tests/DeskNest.Tests -c Release
dotnet run --project tests/DeskNest.UiSmoke -c Release
dotnet build DeskNest.sln -c Release
```

发布输出位于 `dist/`。未安装 Inno Setup 时只生成发布目录与 ZIP，不生成新的 Setup。二进制安装包通过 GitHub Releases 分发，不纳入 Git 源码历史。

## 项目结构

```text
src/DeskNest.App/       WPF 界面、桌面挂载、系统集成
src/DeskNest.Core/      数据模型、布局算法、分类与持久化
tests/DeskNest.Tests/   核心逻辑回归测试
tests/DeskNest.UiSmoke/ WPF 控件冒烟测试
scripts/               构建与用户级安装脚本
docs/                  使用指南、验证说明与设计记录
```

## 已知边界与问题反馈

- 单显示器为主要使用基线；多显示器、混合 DPI 与热插拔仍需进一步实机验收。
- Explorer 更新或重启可能影响桌面挂载，项目提供挂载失败时的降级逻辑。
- 当前自动测试不等于完整的真实桌面拖拽与视觉验收，详见 [质量报告](docs/QUALITY-REPORT.md)。

提交问题时请附上 Windows 版本、显示器缩放比例、软件版本、复现步骤，以及不含隐私信息的截图。不要直接上传包含私人路径的 `state.json`。
