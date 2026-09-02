# DeskBox Agent/MCP 控制说明

DeskBox 启动后会为当前 Windows 用户开放一个本地命名管道。每次连接发送一行 JSON
请求，并接收一行 JSON 响应。该接口只在本机使用，不开放网络端口。

本项目中的 `src/DeskBox.Mcp` 是 stdio MCP 适配器。AI 客户端启动它以后，适配器会把
MCP 工具调用转发给正在运行的 DeskBox 本体。

## 安装和连接

发布版分成两个独立安装程序：

- `DeskBox-Agent-*-App-win-x64.exe`：DeskBox 本体，可独立使用。
- `DeskBox-Agent-*-MCP-win-x64.exe`：AI 控制扩展，按需安装，可单独卸载。

安装 MCP 后，在 ChatGPT 的 MCP 设置中填写：

```text
名称：deskbox
命令：<DeskBox 安装目录>\mcp\DeskBox.Mcp.exe
参数：留空
工作目录：<DeskBox 安装目录>\mcp
```

连接前应先运行 `DeskBox.exe`。MCP 由 AI 客户端在连接时启动，断开连接后可以结束；
仅使用 DeskBox 本体时不需要运行 MCP。

## 本地测试

启动 Debug 版本后，可运行：

```powershell
.\scripts\Test-DeskBoxAgentPipe.ps1 -Method ping
.\scripts\Test-DeskBoxAgentPipe.ps1 -Method get_app_status
```

正式版管道名是 `DeskBox_Agent_7F3A9B2E`。开发数据目录使用
`DESKBOX_DEV_DATA_ROOT` 生成隔离管道名，实际名称可通过 `get_app_status` 查看。

请求示例：

```json
{"id":"1","method":"list_todos","params":{}}
```

成功响应：

```json
{"id":"1","ok":true,"result":[]}
```

失败响应：

```json
{"id":"1","ok":false,"error":{"code":"invalid_argument","message":"..."}}
```

## 主要能力

### 状态和读取

- `ping`：检查连接。
- `get_capabilities`：读取支持的能力。
- `get_app_status`：读取 DeskBox 运行状态和管道信息。
- `list_widgets`：读取所有格子。
- `list_widget_items(widgetId)`：读取指定文件格子中的文件、文件夹和快捷方式。
- `list_desktop_items`：扫描当前用户桌面。
- `scan_public_desktop`：扫描公共桌面 `C:\Users\Public\Desktop`。
- `get_widget_layout`：读取格子位置、尺寸、显示、折叠和锁定状态。

### 桌面整理

- `preview_organize_desktop`：按内置类别预览桌面整理。
- `preview_custom_organize_desktop`：由 AI 按用户要求生成 1 至 4 个分类和新格子。
- `preview_organize_desktop_to_widget`：把文件或顶层文件夹整理到指定现有格子。
- `preview_organize_desktop_to_widgets`：一次预览多组“文件到现有格子”的映射。
- `apply_organize_plan`：确认后执行预览计划。

AI 自定义分类时，应先读取桌面项目，再把准确的源路径交给预览工具。预览与执行分离，
计划执行、被新计划替换或 DeskBox 退出后失效。

### 格子项目和重复项

- `move_widget_items`：在现有文件格子之间移动项目。
- `rename_widget_item`：重命名格子项目。
- `remove_widget_items`：移出项目，默认进入回收站。
- `preview_deduplicate_widgets`：按快捷方式目标和启动参数预览重复项。
- `apply_deduplicate_plan`：确认后把重复项移到可恢复隔离区。

### 布局

- `preview_widget_layout`：预览位置、尺寸、对齐、等间距和锁定调整。
- `apply_widget_layout`：确认后应用布局计划。

### Shell 系统入口

- `ensure_shell_system_entry`：在指定文件格子中幂等创建或更新“此电脑”“回收站”
  “网络”“控制面板”或“用户文件”入口。
- `set_shell_system_icon_visibility`：单独显示或隐藏原桌面的系统图标。

### 待办

- `list_todos`、`create_todo`、`update_todo`、`complete_todo`。
- `delete_todo`、`restore_todo`、`set_todo_importance`、`set_todo_due_date`。
- `reorder_todo`：按从 0 开始的位置调整顺序并持久化。

### 历史和撤销

- `list_operation_history`：读取最近操作。
- `preview_undo_operation`：预览指定操作或最近一次可撤销操作。
- `undo_last_operation`：确认后撤销最近操作。
- `undo_operation`：通过已知 `historyId` 撤销指定操作。

## 安全规则

移动、删除、重命名文件，修改布局，隐藏系统图标和撤销操作必须先预览。MCP 应保留
`confirmation_required` 响应，并由 AI 向用户展示计划、取得明确确认后再执行。

MCP 不直接修改 DeskBox 的 JSON 数据文件。DeskBox 自己的事务日志、整理历史和恢复机制
始终是文件操作的最终依据。

## 开发和打包

开发时运行 MCP：

```powershell
dotnet run --project .\src\DeskBox.Mcp\DeskBox.Mcp.csproj
```

生成 x64 本体、MCP 的独立 EXE/ZIP 和 SHA256 文件：

```powershell
.\scripts\publish-agent-release.ps1 -Platform x64 -Version 1.4.8-agent.3
```

产物位于 `.artifacts\agent-release\win-x64`。发布包不包含用户设置、格子数据、待办、
随记或桌面文件。换电脑和数据备份方法见 [发布包中文说明](agent-release.md)。
