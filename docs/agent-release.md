# DeskBox Agent/MCP 非官方修改版

这是基于 DeskBox 的非官方 Agent/MCP 修改版，不是原作者发布的官方版本。
上游项目：<https://github.com/Tianyu199509/DeskBox>

## 使用方法

1. 将整个压缩包解压到一个固定目录，例如 `D:\Apps\DeskBox-Agent`。
2. 运行根目录中的 `DeskBox.exe`。
3. 在 ChatGPT 的 MCP 设置中添加一个 stdio 服务器：
   - 名称：`deskbox`
   - 命令：`D:\Apps\DeskBox-Agent\mcp\DeskBox.Mcp.exe`
   - 参数：留空
   - 工作目录：`D:\Apps\DeskBox-Agent\mcp`
4. 重新连接 MCP，然后让 AI 调用 `ping` 或 `get_app_status` 检查连接。

请按照你的实际解压位置修改命令和工作目录。MCP 进程由 AI 客户端按需启动，
DeskBox 主程序需要先运行。

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
