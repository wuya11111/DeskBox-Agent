# DeskBox Agent/MCP 非官方修改版

这是基于 DeskBox 的非官方 Agent/MCP 修改版，不是原作者发布的官方版本。
上游项目：<https://github.com/Tianyu199509/DeskBox>

## 安装方法

MCP 已与 DeskBox 本体拆分。请先安装 DeskBox 本体，再安装 MCP 扩展：

1. 双击 `DeskBox-Agent-*-App-win-x64.exe` 安装本体。
2. 需要 AI 控制时，再双击 `DeskBox-Agent-*-MCP-win-x64.exe`。安装程序会自动寻找本体目录。
3. 在 ChatGPT 的 MCP 设置中添加一个 stdio 服务器：
   - 名称：`deskbox`
   - 命令：`DeskBox 安装目录\mcp\DeskBox.Mcp.exe`
   - 参数：留空
   - 工作目录：`DeskBox 安装目录\mcp`
4. 重新连接 MCP，然后让 AI 调用 `ping` 或 `get_app_status` 检查连接。

如果使用 ZIP 便携包，请完整解压，然后将命令和工作目录改为实际解压位置。
MCP 进程由 AI 客户端按需启动，DeskBox 主程序需要先运行。

## 不需要 MCP 时

只下载并安装名称中带 `App` 的本体安装包即可。纯本体包不包含
`DeskBox.Mcp.exe`，不会产生 MCP 进程。以后可以随时单独安装或卸载 MCP；卸载 MCP
不会删除 DeskBox 本体、格子、待办、随记或桌面文件。

## 换电脑时的数据迁移

程序包不包含个人格子、待办、随记或桌面文件。它们默认保存在：

```text
%LOCALAPPDATA%\DeskBox\data
```

退出 DeskBox 后，可以将该目录单独备份到私密存储。不要把个人数据上传到公开的
GitHub Release。不同电脑上的文件路径可能不同，恢复后应检查文件格子的目标路径。

## 安全确认

移动、删除、重命名文件，修改格子布局，隐藏系统桌面图标以及撤销操作都应先预览，
并在 AI 明确展示计划后由用户确认。MCP 不应直接修改 DeskBox 的 JSON 数据文件。

本修改版及其对应源码按照 `GPL-3.0-only` 发布。详情请参阅同目录的 `LICENSE` 和
`LICENSE_CHANGE.md`。
