import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';

const toolName = 'take_screenshot';
const toolDescription =
  'Takes a screenshot of a Unity Editor window. ' +
  'Supports "scene" (Scene View), "game" (Game View), or "editor" (full editor window via Windows API). ' +
  'Returns the image as base64-encoded PNG.';

const paramsSchema = z.object({
  target: z
    .enum(['scene', 'game', 'editor'])
    .optional()
    .describe(
      'Which view to capture: "scene" for Scene View, "game" for Game View, "editor" for the full editor window (Windows API fallback). Defaults to "game".'
    ),
  maxWidth: z
    .number()
    .int()
    .min(64)
    .max(1920)
    .optional()
    .describe('Maximum width of the output image in pixels (default: 1920). Image is downscaled preserving aspect ratio.'),
  maxHeight: z
    .number()
    .int()
    .min(64)
    .max(1080)
    .optional()
    .describe('Maximum height of the output image in pixels (default: 1080). Image is downscaled preserving aspect ratio.'),
});

export function registerTakeScreenshotTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: any) {
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params,
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to take screenshot'
    );
  }

  // Return as MCP image content if we have image data, otherwise text fallback
  if (response.imageData && response.mimeType) {
    return {
      content: [
        {
          type: 'image' as const,
          data: response.imageData,
          mimeType: response.mimeType,
        },
        {
          type: 'text' as const,
          text: response.message || 'Screenshot captured successfully',
        },
      ],
    };
  }

  // Text-only fallback
  return {
    content: [
      {
        type: 'text' as const,
        text: response.message || 'Screenshot captured but no image data returned',
      },
    ],
  };
}
