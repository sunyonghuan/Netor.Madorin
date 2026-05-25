namespace DesktopPet.Ai;

internal static class PetMcpJson
{
    public const string InitializeResult = """
        {
          "protocolVersion": "2025-11-25",
          "serverInfo": {
            "name": "DesktopPet",
            "version": "0.1.0"
          },
          "capabilities": {
            "tools": {}
          }
        }
        """;

    public const string ToolsListResult = """
        {
          "tools": [
            {
              "name": "pet.show",
              "description": "Show the desktop pet window.",
              "inputSchema": {
                "type": "object",
                "properties": {}
              }
            },
            {
              "name": "pet.hide",
              "description": "Hide the desktop pet window.",
              "inputSchema": {
                "type": "object",
                "properties": {}
              }
            },
            {
              "name": "pet.say",
              "description": "Set the pet to speaking state and update subtitle text.",
              "inputSchema": {
                "type": "object",
                "properties": {
                  "text": {
                    "type": "string"
                  }
                }
              }
            },
            {
              "name": "pet.think",
              "description": "Set the pet to thinking state and update subtitle text.",
              "inputSchema": {
                "type": "object",
                "properties": {
                  "text": {
                    "type": "string"
                  }
                }
              }
            },
            {
              "name": "pet.status",
              "description": "Get current pet state and subtitle snapshot.",
              "inputSchema": {
                "type": "object",
                "properties": {}
              }
            }
          ]
        }
        """;
}
