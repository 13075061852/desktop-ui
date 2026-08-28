# 栖格 · DeskNest

一款面向 Windows 10/11 的轻量桌面图标分区工具。DeskNest 使用安全映射整理桌面项目，不移动、不删除、不重命名真实文件。

## MVP 功能

- Fluent 风格桌面分区盒子
- 一键按应用、文档、图片、文件夹和其他项目分类
- 文件拖入与分区间移动
- 双击打开、右键定位、移出映射
- 分区拖动、调整尺寸、折叠与锁定
- 临时隐藏/恢复 Windows 原桌面图标
- 托盘运行、本地 JSON 持久化
- Explorer 桌面层挂载失败时安全降级

## 快速构建

```powershell
npm run build
```

该命令会自动还原项目、运行测试、构建最新 Release 版 `exe`，并生成 ZIP 安装包。

发布产物位于 `dist\DeskNest-win-x64`。项目不依赖第三方 NuGet 包。

详细使用方式见 [`docs/USER-GUIDE.md`](docs/USER-GUIDE.md)。
