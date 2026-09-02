# DeskBox Agent 本体（非官方修改版）

这是 DeskBox Agent 的纯本体版本，不包含 MCP 程序。它基于 DeskBox 开源项目修改，
不是原作者发布的官方版本。

上游项目：<https://github.com/Tianyu199509/DeskBox>

## 安装和使用

- 使用 EXE：双击 `DeskBox-Agent-*-App-win-x64.exe`，按照中文安装向导操作。
- 使用 ZIP：完整解压到固定目录，再运行 `DeskBox.exe`，不要直接在压缩包内运行。
- 本体可独立完成格子、桌面整理、待办、随记、搜索、天气和音乐等功能。
- 本体包不包含 `DeskBox.Mcp.exe`，也不会启动 MCP 进程。

以后需要 AI 控制时，再下载同一版本的 `DeskBox-Agent-*-MCP-win-x64.exe`。
MCP 扩展可以独立卸载，不会删除本体、格子、待办或随记。

## 本地数据

个人设置、格子、待办和随记默认保存在：

```text
%LOCALAPPDATA%\DeskBox\data
```

安装、升级或卸载 MCP 不会删除这个目录。换电脑时，请先退出 DeskBox，再把该目录
备份到私密存储。不要把个人数据上传到公开的 GitHub Release。

本修改版按照 `GPL-3.0-only` 发布，详情见同目录的 `LICENSE` 和
`LICENSE_CHANGE.md`。
