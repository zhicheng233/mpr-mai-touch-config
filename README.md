# maimai 4× MPR121 触摸控制器 (mpr-mai-touch)

基于 **Seeed XIAO SAMD21** 与 **4 块 MPR121**（共 48 通道）的 maimai 34 键电容触摸解决方案，包含嵌入式固件与 Avalonia 跨平台桌面调试工具。

---

## 特性

- **街机兼容**：标准 9 字节游戏输出帧（`(` + 7 字节 touch data + `)`），34 键对应 bit 0~33；支持街机控制指令（`{xxEx}` 复位、`{xxAx}` 阈值等）。
- **单串口复用**：游戏通信与上位机配置共用一个 USB CDC 端口，无需多线或拨码切换。
- **自定义映射**：支持 48 个物理通道到 34 键自由映射，固件板载持久化。
- **掉电安全**：SAMD21 双 Flash 扇区轮换备份配置，彻底避免保存中断电丢数据。
- **精准去抖与防粘键**：调优基线追踪参数，杜绝高速连按引起的基线爬升与粘键卡死；直接读取 MPR121 硬件触摸状态，扫描帧率数百 FPS。
- **现代上位机**：34 键 SVG 动态光圈、通道实时遥测、PCB 图形化映射编辑器、引导式阈值校准、一键固件刷写、中英双语。
- **MCP 支持**：支持 Model Context Protocol（内置 HTTP / stdio），支持 AI 助手自动读取遥测并调优参数。

---

## 目录结构

```text
├── firmware/              # 固件源码 (PlatformIO, Seeed XIAO SAMD21)
├── software/              # 上位机源码 (C# / Avalonia 11, .NET 8)
├── protocol/PROTOCOL.md   # 通信协议规范 (帧格式、CRC16、指令集)
├── touch/                 # 34 键面板 SVG 素材
└── maimaiTouchControl/    # 原始参考固件 ( dkmaiproj，用于对比)
```

---

## 快速上手

### 1. 硬件接线简述

4 片 MPR121 挂载在同一组 I2C（SDA / SCL），通过 `ADDR` 引脚区分地址：
- `0x5A`（ADDR→GND）、`0x5B`（ADDR→3.3V）、`0x5C`（ADDR→SDA）、`0x5D`（ADDR→SCL）。
- 详见 [firmware/README.md](firmware/README.md)。

### 2. 固件编译与刷写

```bash
cd firmware
pio run -e seeed_xiao
```
固件产物：`.pio/build/seeed_xiao/firmware.bin`。可在上位机「固件」标签页一键刷写，或用 bossac 刷入。

### 3. 运行上位机

```bash
cd software/MaimaiTouchTool
dotnet run
```
连接设备 COM 口后即可查看实时响应、调校阈值或修改键位映射。

---

## 文档

- [固件与硬件接线说明](firmware/README.md)
- [桌面工具使用与构建说明](software/README.md)
- [二进制协议规范](protocol/PROTOCOL.md)
- [MCP 客户端接入指南](software/MaimaiTouchTool/Mcp/README.md)
