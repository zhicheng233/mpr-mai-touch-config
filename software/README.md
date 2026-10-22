# 桌面配置工具 (software)

基于 Avalonia 11 (.NET 8) 的 maimai 触摸控制器桌面配置、测试与调优工具。

---

## 主要功能

- **34 键实时视窗**：动态渲染 SVG 光圈面板，高亮展示当前击键；可监听原生游戏 9 字节帧速率。
- **48 通道实时遥测**：直方图展示各通道 Filtered、Baseline、Drop 及触发阈值，支持选中通道查看波形曲线。
- **PCB 图形化映射编辑器**：按 4 块板卡 12 通道排布，支持键位拖拽互换、防冲突保护、自动学习（按 A1~E8 顺序按压标定）与快照历史回滚。
- **引导式校准**：通过静置采集噪声 + 击键标定，自动计算推荐触发与释放阈值。
- **一键固件刷写**：集成 bossac，自动处理 1200bps 重启与固件烧录。
- **MCP 服务支持**：支持内置 HTTP 端点（默认 8477）和 `--mcp` 命令行模式，允许 AI 助手自动化读取并调参（详见 [Mcp/README.md](MaimaiTouchTool/Mcp/README.md)）。
- **多语言**：支持中英文无缝切换。

---

## 工程结构

- `MaimaiTouchTool/`：Avalonia 主应用程序。
  - `Touch/`：SVG 贴图渲染与按键布局。
  - `Protocol/`：协议 1.1 编解码器与校验。
  - `Transport/`：串口通信、指令队列与超时保护。
  - `Mcp/`：MCP 服务端实现。
  - `ViewModels/`：各功能页面视图模型。
- `MaimaiTouchTool.Tests/`：单元测试。

---

## 构建与运行

需安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
# 运行
dotnet run --project MaimaiTouchTool/MaimaiTouchTool.csproj

# 测试
dotnet test

# 打包发布 (Windows x64 单文件)
dotnet publish MaimaiTouchTool/MaimaiTouchTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
产物输出至：`MaimaiTouchTool/bin/Release/net8.0/win-x64/publish/MaimaiTouchTool.exe`。
