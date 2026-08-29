using System.Text.Json;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class McpTransportChecks
{
    public static async Task RunAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        string repositoryRoot = FindRepositoryRoot();
        string serverAssembly = Path.Combine(
            repositoryRoot,
            "src",
            "ConsoleControl.Mcp",
            "bin",
            "Debug",
            "net10.0",
            "ConsoleControl.Mcp.dll");
        Require(File.Exists(serverAssembly), $"MCP server assembly was not copied to {serverAssembly}.");

        await using McpClient client = await McpClient.CreateAsync(
            new StdioClientTransport(new()
            {
                Name = "ConsoleControl contract test",
                Command = "dotnet",
                Arguments = [serverAssembly, "--daemon", "http://127.0.0.1:1"],
                WorkingDirectory = repositoryRoot,
            }),
            cancellationToken: timeout.Token);

        Require(client.ServerInstructions?.Contains("Begin with console_get_status", StringComparison.Ordinal) == true,
            "MCP initialization did not explain the discovery workflow.");

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Require(tools.Any(tool => tool.Name == "console_get_status"), "console_get_status was not published.");
        Require(tools.Any(tool => tool.Name == "console_get_screenshot"), "console_get_screenshot was not published.");
        McpClientTool sequenceTool = tools.Single(tool => tool.Name == "console_run_sequence");
        Require(!JsonSerializer.Serialize(sequenceTool).Contains("screenshotFidelity", StringComparison.Ordinal),
            "console_run_sequence still publishes the removed screenshotFidelity parameter");

        CallToolResult status = await client.CallToolAsync(
            "console_get_status",
            cancellationToken: timeout.Token);
        Require(status.IsError == true, "status against an unreachable daemon should be a tool error.");
        TextContentBlock text = status.Content.OfType<TextContentBlock>().First();
        using JsonDocument envelope = JsonDocument.Parse(text.Text);
        JsonElement root = envelope.RootElement;
        Require(root.GetProperty("outcome").GetString() == "daemon_unavailable",
            "unreachable daemon did not return the stable daemon_unavailable outcome.");
        Require(root.TryGetProperty("detail", out _), "result envelope omitted detail.");
        Require(root.GetProperty("recovery").GetString()?.Length > 0, "error envelope omitted recovery.");
        Require(root.GetProperty("retryable").GetBoolean(), "daemon_unavailable was not marked retryable.");
        Require(root.TryGetProperty("data", out _), "result envelope omitted data.");

        CallToolResult invalidPress = await client.CallToolAsync(
            "console_press",
            new Dictionary<string, object?>
            {
                ["control"] = "a",
                ["durationMs"] = 0,
            },
            cancellationToken: timeout.Token);
        Require(invalidPress.IsError == true,
            "an invalid press duration should be a tool error");
        TextContentBlock invalidText = invalidPress.Content.OfType<TextContentBlock>().First();
        using JsonDocument invalidEnvelope = JsonDocument.Parse(invalidText.Text);
        Require(invalidEnvelope.RootElement.GetProperty("outcome").GetString() == "invalid_request",
            "an invalid press duration escaped the structured error envelope");

        foreach (string stickClick in new[] { "left_stick_click", "right_stick_click" })
        {
            CallToolResult stickClickPress = await client.CallToolAsync(
                "console_press",
                new Dictionary<string, object?>
                {
                    ["control"] = stickClick,
                    ["durationMs"] = 80,
                },
                cancellationToken: timeout.Token);
            TextContentBlock stickClickText = stickClickPress.Content.OfType<TextContentBlock>().First();
            using JsonDocument stickClickEnvelope = JsonDocument.Parse(stickClickText.Text);
            Require(stickClickEnvelope.RootElement.GetProperty("outcome").GetString() == "control_required",
                $"the MCP server did not recognize {stickClick}");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ConsoleControl.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the ConsoleControl repository root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}