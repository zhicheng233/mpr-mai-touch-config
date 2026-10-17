# MCP 接入教程

`MaimaiTouchTool.exe --mcp` 会以 **MCP (Model Context Protocol) stdio 服务器** 模式启动，
不打开 GUI，只暴露一组工具给 AI 模型，让模型可以自己完成
「连接 → 读配置 → 调阈值 / 改映射 → 应用 → 保存 → 校准 → 读遥测」整条链路。

代码在 [`McpServer.cs`](./McpServer.cs)，复用 `SerialTransport` + `ConfigurationWorkflow`，
所以协议层和 GUI 完全一致。

---

## 0. 前置条件

| 项 | 要求 |
| --- | --- |
| 固件 | 已烧录新固件（协议 1.1）。`set_mapping` 需要能力位 bit4，旧固件会被拒绝 |
| 硬件 | XIAO SAMD21 通过 USB 接到电脑，记下 COM 号（设备管理器 / `MaimaiTouchTool` 的串口下拉框） |
| GUI | **必须关闭** `MaimaiTouchTool` 主程序 —— 一个 CDC 串口同时只能被一方占用 |
| 构建 | 见下方「构建」 |

### 构建

```powershell
dotnet build software/MaimaiTouchTool/MaimaiTouchTool.csproj -c Release
```

产物：

```
software\MaimaiTouchTool\bin\Release\net8.0\MaimaiTouchTool.exe
```

建议用绝对路径配置客户端（MCP 客户端的工作目录不一定是你以为的那个）。
先确认这个路径存在，下面的配置都要填它。

---

## 1. 配置客户端

MCP 服务器是 **stdio** 类型：客户端负责启动进程并通过 stdin/stdout 收发 JSON-RPC。
所以配置里只要写「怎么启动这个 exe」+ 参数 `--mcp` 即可，不需要端口号。

### OpenCode

编辑项目根目录的 `opencode.json`（或全局 `~/.config/opencode/opencode.json`）：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "maimai-touch": {
      "type": "local",
      "command": [
        "F:\\Code\\mpr-mai-touch-config\\software\\MaimaiTouchTool\\bin\\Release\\net8.0\\MaimaiTouchTool.exe",
        "--mcp"
      ],
      "enabled": true
    }
  }
}
```

然后重启 OpenCode，或者在对话里直接说「用 maimai-touch 工具读一下当前配置」。
工具名会带服务器前缀，例如 `connect`、`get_config`。

### Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "maimai-touch": {
      "command": "F:\\Code\\mpr-mai-touch-config\\software\\MaimaiTouchTool\\bin\\Release\\net8.0\\MaimaiTouchTool.exe",
      "args": ["--mcp"]
    }
  }
}
```

改完完全退出 Claude Desktop（托盘也要退）再重开。

### Cursor / 其他支持 `mcpServers` 的客户端

```json
{
  "mcpServers": {
    "maimai-touch": {
      "command": "F:\\Code\\mpr-mai-touch-config\\software\\MaimaiTouchTool\\bin\\Release\\net8.0\\MaimaiTouchTool.exe",
      "args": ["--mcp"]
    }
  }
}
```

### Windows 路径注意

JSON 里反斜杠要写成 `\\`。如果客户端不支持直接启动 exe，可以套一层：

```json
{ "command": "cmd", "args": ["/c", "F:\\...\\MaimaiTouchTool.exe", "--mcp"] }
```

> 直接 spawn exe 是可行的（已实测，`OutputType=WinExe` 不影响 stdin/stdout 重定向）。
> `cmd /c` 只在**手工调试**时为了做 `< in.txt > out.txt` 重定向才需要。

---

## 2. 验证连通

不想开客户端就想确认服务器能跑，直接在 PowerShell 里喂三条消息：

```powershell
$exe = "F:\Code\mpr-mai-touch-config\software\MaimaiTouchTool\bin\Release\net8.0\MaimaiTouchTool.exe"
$dir = "$env:TEMP\mcp_probe"; New-Item -ItemType Directory -Force $dir | Out-Null
@'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
'@ | Set-Content "$dir\in.txt" -Encoding utf8
cmd /c "`"$exe`" --mcp < `"$dir\in.txt`" > `"$dir\out.txt`""
Get-Content "$dir\out.txt"
```

正常输出（第一行是握手，第二行是 10 个工具的定义）：

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"maimai-touch","version":"0.1.0"}}}
{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"connect",...}, ...]}}
```

---

## 3. 工具一览

所有工具返回 `{"content":[{"type":"text","text":"..."}],"isError":false}`。
出错时 `isError` 为 `true`，`text` 以 `ERROR:` 开头 —— 让模型读这段文本就能自行纠正。

| 工具 | 参数 | 说明 |
| --- | --- | --- |
| `connect` | `port` (必填) | 打开串口并进入配置模式。容忍「已处于配置模式」 |
| `disconnect` | — | 发 EXIT_CFG 并断开串口 |
| `get_config` | — | 读当前**已应用**配置（未连接会报错） |
| `set_board` | `board`(1-4) 必填；`baseThreshold`(1-255)、`sensOffset`(-127..127)、`touchDebounce`(0-7)、`releaseDebounce`(0-7)、`sfi`(0-3)、`esi`(0-7) 可选 | 暂存某块板的响应参数，未传字段保持原值 |
| `set_electrode` | `key`(A1..E8)、`offset`(-127..127) | 暂存某游戏键的电极偏移 |
| `set_mapping` | `key`(A1..E8)、`board`(1-4)、`channel`(0-11) | 暂存某键的板/通道映射 |
| `apply` | — | SET_CFG + APPLY_CFG + 读回校验，暂存生效 |
| `save` | — | SAVE_CFG，写入 EEPROM（掉电保持） |
| `calibrate` | — | 触发自动校准，等 CAL_DONE（最长 61 s） |
| `read_telemetry` | `durationMs`（默认 1000） | 流式读遥测，返回 48 通道 filtered/baseline + 触摸位图 |

**暂存 → 应用 → 保存** 是三段式：`set_*` 只改内存里的 staged 副本，
`apply` 才下发到板子，`save` 才落 EEPROM。不 `apply` 就 `get_config` 看到的是旧值。

---

## 4. 典型流程

### 读当前状态

```
connect(port="COM10")
get_config()
```

返回 JSON：

```json
{
  "boards": [{"baseThreshold":20,"sensOffset":0,"touchDebounce":1,"releaseDebounce":1,"sfi":2,"esi":0}, ...(4 块)],
  "electrodeOffsets": [-3,-3,-3, ...(34 个)],
  "keyMap": [{"board":4,"channel":4}, {"board":4,"channel":0}, ...(34 个)]
}
```

`keyMap` 按**键序**排列（A1..A8, B1..B8, C1..C2, D1..D8, E1..E8），
`board` 是 1-4（= 地址 0x5A + board-1），`channel` 是 0-11。

### 调灵敏度

负的偏移 = 更灵敏。芯片分辨率是 4，建议按 4 的倍数调。

```
connect(port="COM10")
set_board(board=2, baseThreshold=24)      # 只改这一项，其余不动
set_electrode(key="C1", offset=-8)        # 单独把这个键调灵一点
apply()
read_telemetry(durationMs=2000)           # 看 filtered 掉多少
save()                                    # 满意了再落盘
```

运行时阈值模型：`touchDelta = clamp(baseThreshold + sensOffset + electrodeOffset, 1, 1023)`，
芯片寄存器写 `ceil(touchDelta/4)`。

### 改映射

板子焊接顺序和参考固件不同时用它纠正。**34 个键的通道必须互不重复**，否则拒绝。

```
connect(port="COM10")
get_config()                              # 先看现有映射
set_mapping(key="A1", board=1, channel=0)
apply()
save()
```

> 单次 `set_mapping` 要求最终全表仍合法。要交换两个键，得一次性规划好，
> 或者先把其中一个挪到空通道（48 个通道里有 14 个是空的）。
> 工具里那套「自动对调」逻辑在 `ConfigurationEditorModel.AssignChannel`，MCP 侧没有等价封装。

### 校准

```
connect(port="COM10")
calibrate()          # 板子要没人碰；最长等 61 s
save()
```

---

## 5. 常见问题

**`ERROR: not connected; call connect first`**
没调 `connect`，或者中间 `disconnect` 过。

**`ERROR: GET_CFG rejected (streaming? ...)`**
板子还在游戏模式流式输出。先 `disconnect()` 再 `connect()`。

**`ERROR: ENTER_CFG rejected: ...`**
只有 `AlreadyConfigured` 会被容忍（工具已经处理）。其他状态码说明板子状态异常，重新插拔 USB。

**`ERROR: mapping conflict: ...`**
`set_mapping` 让两个键占了同一个通道。先 `get_config` 看现状再改。

**`ERROR: unknown key AX`**
键名只接受 `A1..A8, B1..B8, C1, C2, D1..D8, E1..E8`（34 个，大小写不敏感）。

**工具调用超时**
`calibrate` 最长 61 s，`read_telemetry` 取决于 `durationMs`。
客户端默认的工具超时可能只有几十秒 —— 把 `durationMs` 调小，或调大客户端超时。

**服务器进程退不干净 / 板子卡在配置模式**
MCP 服务器在 stdin 关闭时会尝试 EXIT_CFG 再断开。
如果客户端直接杀进程，板子会留在配置模式，约 30 s 空闲后固件自动超时退出，
或者重新插拔 USB 也行。

**`--mcp` 启动了还是有窗口闪一下**
`OutputType` 是 `WinExe`，进程本身不创建窗口，闪的可能是客户端起的控制台宿主。
不影响功能。设 `MAIMAI_SERIAL_LOG=1` 可以让串口收发写进 `%TEMP%\maimai_serial.log`。

**想同时用 GUI 和 MCP**
不行 —— 单 USB CDC 口复用，两者互斥。MCP 服务器占着串口时 GUI 连不上，反之亦然。
