# DeskBox local agent control

DeskBox exposes a local, current-user-only named pipe after startup. The pipe
uses one newline-delimited JSON request followed by one newline-delimited JSON
response per connection.

For a local smoke test after starting a Debug build:

```powershell
.\scripts\Test-DeskBoxAgentPipe.ps1 -Method ping
.\scripts\Test-DeskBoxAgentPipe.ps1 -Method get_app_status
```

The production pipe name is `DeskBox_Agent_7F3A9B2E`. Development data roots use
a deterministic scope derived from `DESKBOX_DEV_DATA_ROOT`; the active name is
returned by `get_app_status`.

## Request

```json
{"id":"1","method":"list_todos","params":{}}
```

`params` is optional. Responses have this shape:

```json
{"id":"1","ok":true,"result":[]}
```

Errors use:

```json
{"id":"1","ok":false,"error":{"code":"invalid_argument","message":"..."}}
```

## Commands

- `ping`
- `get_capabilities`
- `get_app_status`
- `list_widgets`
- `list_desktop_items` with optional `includeSlowItems`; entries excluded only
  because their reason is `Folder` may still be passed to
  `preview_organize_desktop_to_widget`.
- `scan_public_desktop` with optional `includeSlowItems`; returns top-level items
  from `C:\Users\Public\Desktop` without treating them as automatically
  excluded public entries.
- `list_todos` with optional `widgetId`
- `create_todo` with `title`, optional `widgetId`, `important`, and ISO-8601 `dueDate`
- `update_todo` with `itemId` and any of `title`, `important`, or `dueDate`
- `delete_todo`, `restore_todo`, `set_todo_importance`, and `set_todo_due_date`
- `reorder_todo` with a zero-based `index`; ordering is persisted
- `complete_todo` with `itemId` and optional `widgetId`
- `preview_organize_desktop` with optional `includeSlowItems`
- `preview_custom_organize_desktop` with `groups` (1-4 groups, each with a
  display `name` and the exact desktop `sourcePaths` to include), then use
  `apply_organize_plan` to create those file widgets and move the files.
- `preview_organize_desktop_to_widget` with `widgetId` and a non-empty
  `sourcePaths` array. The paths may come from the user or Public Desktop and
  may identify files or top-level folders; the preview targets the existing
  active File widget and never creates a new widget. Apply it with
  `apply_organize_plan` and `confirm: true`.
- `preview_organize_desktop_to_widgets` with `mappings`, where each mapping has
  a `widgetId` and `sourcePaths`; all mappings share one preview/apply
  transaction and one history entry.
- `ensure_shell_system_entry` (the legacy `create_shell_system_entry` name is
  still accepted) with an existing File widget `widgetId`, one of
  `this_pc`, `recycle_bin`, `network`, `control_panel`, or `user_files`, and
  explicit `confirm: true`. It creates or updates one `.lnk` Shell namespace
  entry inside the widget folder. Set `hideDesktopIcon: true` to hide the
  matching original Windows desktop system icon.
- `set_shell_system_icon_visibility` with `systemId`, `hidden`, and explicit
  `confirm: true` to independently show or hide the original desktop icon.
- `list_widget_items` with a File widget `widgetId`; returns top-level files,
  folders, and shortcuts, including shortcut target and arguments.
- `move_widget_items` with `sourceWidgetId`, `targetWidgetId`, `itemPaths`, and
  explicit `confirm: true` to move items between existing File widgets.
- `rename_widget_item` with `widgetId`, `itemPath`, `newName`, and explicit
  `confirm: true`.
- `remove_widget_items` with `widgetId`, `itemPaths`, and explicit
  `confirm: true`; it uses the Recycle Bin unless `recycle: false` is passed.
- `preview_deduplicate_widgets` groups duplicate `.lnk` entries by Shell target
  and arguments. `keepRule` can be `first`, `oldest`, `newest`, or
  `shortest_path`; apply the result with `apply_deduplicate_plan` and
  `confirm: true`. Duplicates are moved to a recoverable DeskBox quarantine and
  can be restored with `undo_operation`.
- `get_widget_layout` returns widget positions, sizes, visibility, collapsed
  state, and position/size locks.
- `preview_widget_layout` accepts explicit `updates` and optional `alignment`,
  `spacing`, `lockPosition`, and `lockSize`, then applies with
  `apply_widget_layout` and explicit `confirm: true`.
- `apply_organize_plan` with `planId` and explicit `confirm: true`
- `undo_operation` with `historyId` and explicit confirmation at the AI layer
- `list_operation_history` with optional `maxCount`
- `preview_undo_operation` with optional `historyId`; when omitted it previews
  the latest undoable operation
- `undo_last_operation` with explicit `confirm: true`; `historyId` is optional
  and defaults to the latest undoable operation

Desktop file moves are intentionally split into preview and apply. A plan is
kept in memory until it is applied, replaced by a newer plan, or the app exits.
The existing DeskBox transaction journal and organization history remain the
source of truth for recovery and undo.

For AI-defined classification, call `list_desktop_items` first, group the
returned eligible file paths according to the user's request, and pass those
paths to `preview_custom_organize_desktop`. The custom preview creates one new
managed file widget per group when applied. Folders, hidden/system items,
temporary files, and other ineligible entries are rejected rather than moved.

## AI adapter boundary

An MCP adapter should connect to this pipe and expose the commands as typed
tools. It must preserve the `confirmation_required` response and ask the user
before applying or undoing a file operation. It must not write DeskBox JSON
files directly.

This repository includes a minimal stdio MCP adapter in
`src/DeskBox.Mcp`. Run it during development with:

```powershell
dotnet run --project .\src\DeskBox.Mcp\DeskBox.Mcp.csproj
```

Set `DESKBOX_AGENT_PIPE_NAME` when DeskBox is using a development data root.

## Portable Agent release

Build a portable x64 package containing both the Native AOT DeskBox application
and the Native AOT MCP adapter:

```powershell
.\scripts\publish-agent-release.ps1 -Platform x64 -Version 1.4.8-agent.1
```

The ZIP and its SHA256 file are written to
`.artifacts\agent-release\win-x64`. The ZIP does not include user settings,
widget data, todos, or managed desktop files. See `docs\agent-release.md` for
the new-computer setup and data-backup instructions.
