using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskBox.Mcp;

internal static class Program
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly string PipeName =
        Environment.GetEnvironmentVariable("DESKBOX_AGENT_PIPE_NAME") ??
        "DeskBox_Agent_7F3A9B2E";

    public static async Task Main()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(output, null, -32700, ex.Message);
                continue;
            }

            JsonNode? response = await HandleRequestAsync(request);
            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString());
            }
        }
    }

    private static async Task<JsonNode?> HandleRequestAsync(JsonNode? request)
    {
        if (request is not JsonObject root)
        {
            return Error(null, -32600, "Request must be a JSON object.");
        }

        JsonNode? id = root["id"]?.DeepClone();
        string? method;
        try
        {
            method = root["method"]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return Error(id, -32600, "Request method must be a string.");
        }
        if (string.IsNullOrWhiteSpace(method))
        {
            return Error(id, -32600, "Request method is required.");
        }

        try
        {
            return method switch
            {
                "initialize" => Success(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "deskbox",
                        ["version"] = "1.0.0"
                    }
                }),
                "notifications/initialized" => null,
                "tools/list" => Success(id, new JsonObject { ["tools"] = ToolDefinitions() }),
                "tools/call" => await HandleToolCallAsync(id, root["params"] as JsonObject),
                _ => Error(id, -32601, $"Method '{method}' is not supported.")
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private static async Task<JsonNode> HandleToolCallAsync(JsonNode? id, JsonObject? parameters)
    {
        string? toolName = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Error(id, -32602, "Tool name is required.");
        }

        JsonObject arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        JsonObject deskboxRequest = new()
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = toolName,
            ["params"] = arguments.DeepClone()
        };
        JsonObject deskboxResponse = await InvokeDeskBoxAsync(deskboxRequest);
        bool ok = deskboxResponse["ok"]?.GetValue<bool>() == true;
        string text = deskboxResponse.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return Success(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            },
            ["isError"] = !ok
        });
    }

    private static async Task<JsonObject> InvokeDeskBoxAsync(JsonObject request)
    {
        using var client = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);

        using var reader = new StreamReader(
            client,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            client,
            Encoding.UTF8,
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync(request.ToJsonString());
        string? response = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException("DeskBox returned an empty response.");
        }

        return JsonNode.Parse(response)?.AsObject() ??
            throw new InvalidOperationException("DeskBox returned an invalid response.");
    }

    private static JsonArray ToolDefinitions()
    {
        var tools = new JsonArray
        {
            Tool("ping", "Check whether DeskBox is ready.", ObjectSchema()),
            Tool("get_capabilities", "List DeskBox agent capabilities.", ObjectSchema()),
            Tool("get_app_status", "Read application and widget counts.", ObjectSchema()),
            Tool("list_widgets", "List configured DeskBox widgets.", ObjectSchema()),
            Tool("list_desktop_items", "List top-level desktop items. Folder items can be passed to preview_organize_desktop_to_widget.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["includeSlowItems"] = new JsonObject { ["type"] = "boolean" }
            }
            }),
            Tool("scan_public_desktop", "Scan top-level items from the Windows Public Desktop.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["includeSlowItems"] = new JsonObject { ["type"] = "boolean" }
                }
            }),
            Tool("list_todos", "List Todo items.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["widgetId"] = new JsonObject { ["type"] = "string" }
            }
            }),
            Tool("create_todo", "Create a Todo item.", new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray(JsonValue.Create("title")),
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["widgetId"] = new JsonObject { ["type"] = "string" },
                ["important"] = new JsonObject { ["type"] = "boolean" },
                ["dueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" }
            }
            }),
            Tool("update_todo", "Update a Todo title, importance, or due date.", new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("itemId")),
                ["properties"] = new JsonObject
                {
                    ["itemId"] = new JsonObject { ["type"] = "string" }, ["widgetId"] = new JsonObject { ["type"] = "string" },
                    ["title"] = new JsonObject { ["type"] = "string" }, ["important"] = new JsonObject { ["type"] = "boolean" },
                    ["dueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" }
                }
            }),
            Tool("delete_todo", "Delete a Todo item.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("itemId")), ["properties"] = new JsonObject { ["itemId"] = new JsonObject { ["type"] = "string" }, ["widgetId"] = new JsonObject { ["type"] = "string" } } }),
            Tool("restore_todo", "Restore a completed Todo item.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("itemId")), ["properties"] = new JsonObject { ["itemId"] = new JsonObject { ["type"] = "string" }, ["widgetId"] = new JsonObject { ["type"] = "string" } } }),
            Tool("set_todo_importance", "Set Todo importance.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("itemId"), JsonValue.Create("important")), ["properties"] = new JsonObject { ["itemId"] = new JsonObject { ["type"] = "string" }, ["important"] = new JsonObject { ["type"] = "boolean" }, ["widgetId"] = new JsonObject { ["type"] = "string" } } }),
            Tool("set_todo_due_date", "Set or clear a Todo due date.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("itemId"), JsonValue.Create("dueDate")), ["properties"] = new JsonObject { ["itemId"] = new JsonObject { ["type"] = "string" }, ["dueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["nullable"] = true }, ["widgetId"] = new JsonObject { ["type"] = "string" } } }),
            Tool("reorder_todo", "Move a Todo item to a zero-based list index.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("itemId"), JsonValue.Create("index")), ["properties"] = new JsonObject { ["itemId"] = new JsonObject { ["type"] = "string" }, ["index"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }, ["widgetId"] = new JsonObject { ["type"] = "string" } } }),
            Tool("complete_todo", "Complete a Todo item.", new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray(JsonValue.Create("itemId")),
            ["properties"] = new JsonObject
            {
                ["itemId"] = new JsonObject { ["type"] = "string" },
                ["widgetId"] = new JsonObject { ["type"] = "string" }
            }
            }),
            Tool("preview_organize_desktop", "Preview desktop organization without moving files.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["includeSlowItems"] = new JsonObject { ["type"] = "boolean" }
            }
            }),
            Tool("preview_custom_organize_desktop", "Preview an AI-defined classification. Each group creates a new file widget when applied.", new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray(JsonValue.Create("groups")),
            ["properties"] = new JsonObject
            {
                ["groups"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = 4,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray(JsonValue.Create("name"), JsonValue.Create("sourcePaths")),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["sourcePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                        }
                    }
                },
                ["includeSlowItems"] = new JsonObject { ["type"] = "boolean" }
            }
            }),
            Tool("preview_organize_desktop_to_widget", "Preview organizing selected user or Public Desktop files and folders into an existing File widget. Apply with confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(
                    JsonValue.Create("widgetId"),
                    JsonValue.Create("sourcePaths")),
                ["properties"] = new JsonObject
                {
                    ["widgetId"] = new JsonObject { ["type"] = "string" },
                    ["sourcePaths"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = new JsonObject { ["type"] = "string" }
                    },
                    ["includeSlowItems"] = new JsonObject { ["type"] = "boolean" }
                }
            }),
            Tool("preview_organize_desktop_to_widgets", "Preview one atomic organization operation targeting multiple existing File widgets.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("mappings")), ["properties"] = new JsonObject { ["mappings"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("widgetId"), JsonValue.Create("sourcePaths")) } }, ["includeSlowItems"] = new JsonObject { ["type"] = "boolean" } } }),
            Tool("ensure_shell_system_entry", "Create or update an idempotent Shell system entry.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("widgetId"), JsonValue.Create("systemId"), JsonValue.Create("confirm")), ["properties"] = new JsonObject { ["widgetId"] = new JsonObject { ["type"] = "string" }, ["systemId"] = new JsonObject { ["type"] = "string" }, ["displayName"] = new JsonObject { ["type"] = "string" }, ["hideDesktopIcon"] = new JsonObject { ["type"] = "boolean" }, ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true } } }),
            Tool("set_shell_system_icon_visibility", "Show or hide a Shell system desktop icon.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("systemId"), JsonValue.Create("hidden"), JsonValue.Create("confirm")), ["properties"] = new JsonObject { ["systemId"] = new JsonObject { ["type"] = "string" }, ["hidden"] = new JsonObject { ["type"] = "boolean" }, ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true } } }),
            Tool("create_shell_system_entry", "Create a Windows Shell system entry inside an existing File widget and optionally hide its original desktop icon. Requires confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(
                    JsonValue.Create("widgetId"),
                    JsonValue.Create("systemId"),
                    JsonValue.Create("confirm")),
                ["properties"] = new JsonObject
                {
                    ["widgetId"] = new JsonObject { ["type"] = "string" },
                    ["systemId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(
                            JsonValue.Create("this_pc"),
                            JsonValue.Create("recycle_bin"),
                            JsonValue.Create("network"),
                            JsonValue.Create("control_panel"),
                            JsonValue.Create("user_files"))
                    },
                    ["displayName"] = new JsonObject { ["type"] = "string" },
                    ["hideDesktopIcon"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
                }
            }),
            Tool("list_widget_items", "List files, folders, and shortcuts in a File widget, including shortcut target and arguments.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(JsonValue.Create("widgetId")),
                ["properties"] = new JsonObject { ["widgetId"] = new JsonObject { ["type"] = "string" } }
            }),
            Tool("move_widget_items", "Move items from one existing File widget to another. Requires confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(JsonValue.Create("sourceWidgetId"), JsonValue.Create("targetWidgetId"), JsonValue.Create("itemPaths"), JsonValue.Create("confirm")),
                ["properties"] = new JsonObject
                {
                    ["sourceWidgetId"] = new JsonObject { ["type"] = "string" },
                    ["targetWidgetId"] = new JsonObject { ["type"] = "string" },
                    ["itemPaths"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JsonObject { ["type"] = "string" } },
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
                }
            }),
            Tool("rename_widget_item", "Rename a file or folder inside a File widget. Requires confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(JsonValue.Create("widgetId"), JsonValue.Create("itemPath"), JsonValue.Create("newName"), JsonValue.Create("confirm")),
                ["properties"] = new JsonObject
                {
                    ["widgetId"] = new JsonObject { ["type"] = "string" },
                    ["itemPath"] = new JsonObject { ["type"] = "string" },
                    ["newName"] = new JsonObject { ["type"] = "string" },
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
                }
            }),
            Tool("remove_widget_items", "Remove items from a File widget, sending them to the Recycle Bin by default. Requires confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(JsonValue.Create("widgetId"), JsonValue.Create("itemPaths"), JsonValue.Create("confirm")),
                ["properties"] = new JsonObject
                {
                    ["widgetId"] = new JsonObject { ["type"] = "string" },
                    ["itemPaths"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JsonObject { ["type"] = "string" } },
                    ["recycle"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
                }
            }),
            Tool("preview_deduplicate_widgets", "Preview duplicate .lnk shortcuts by Shell target and arguments.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["widgetIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["keepRule"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(JsonValue.Create("first"), JsonValue.Create("oldest"), JsonValue.Create("newest"), JsonValue.Create("shortest_path")), ["default"] = "first" }
                }
            }),
            Tool("apply_deduplicate_plan", "Apply a duplicate shortcut cleanup plan. Duplicates are moved to a recoverable quarantine and require confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(JsonValue.Create("planId"), JsonValue.Create("confirm")),
                ["properties"] = new JsonObject
                {
                    ["planId"] = new JsonObject { ["type"] = "string" },
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
                }
            }),
            Tool("get_widget_layout", "Read widget positions, sizes, collapsed state, visibility, and lock state.", ObjectSchema()),
            Tool("preview_widget_layout", "Preview widget layout updates, alignment, equal horizontal spacing, and lock changes.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["widgetIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["updates"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("widgetId")) } },
                    ["alignment"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(JsonValue.Create("left"), JsonValue.Create("right"), JsonValue.Create("top"), JsonValue.Create("bottom"), JsonValue.Create("center_horizontal"), JsonValue.Create("center_vertical")) },
                    ["spacing"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
                    ["lockPosition"] = new JsonObject { ["type"] = "boolean" },
                    ["lockSize"] = new JsonObject { ["type"] = "boolean" }
                }
            }),
            Tool("apply_widget_layout", "Apply a previously previewed widget layout. Requires confirm=true.", new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray(JsonValue.Create("planId"), JsonValue.Create("confirm")),
                ["properties"] = new JsonObject
                {
                    ["planId"] = new JsonObject { ["type"] = "string" },
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
                }
            }),
            Tool("apply_organize_plan", "Apply a previewed desktop organization plan. Requires confirm=true.", new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray(JsonValue.Create("planId"), JsonValue.Create("confirm")),
            ["properties"] = new JsonObject
            {
                ["planId"] = new JsonObject { ["type"] = "string" },
                ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true }
            }
            }),
            Tool("undo_operation", "Undo a completed desktop organization operation.", new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray(JsonValue.Create("historyId")),
            ["properties"] = new JsonObject
            {
                ["historyId"] = new JsonObject { ["type"] = "string" }
            }
            }),
            Tool("list_operation_history", "List recent file organization operations.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["maxCount"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 } } }),
            Tool("preview_undo_operation", "Preview the latest or selected undoable operation.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["historyId"] = new JsonObject { ["type"] = "string" } } }),
            Tool("undo_last_operation", "Undo the latest or selected operation. Requires confirm=true.", new JsonObject { ["type"] = "object", ["required"] = new JsonArray(JsonValue.Create("confirm")), ["properties"] = new JsonObject { ["historyId"] = new JsonObject { ["type"] = "string" }, ["confirm"] = new JsonObject { ["type"] = "boolean", ["const"] = true } } })
        };
        return tools;
    }

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };

    private static JsonObject ObjectSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    };

    private static JsonObject Success(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static Task WriteErrorAsync(StreamWriter output, JsonNode? id, int code, string message)
    {
        return output.WriteLineAsync(Error(id, code, message).ToJsonString());
    }
}
