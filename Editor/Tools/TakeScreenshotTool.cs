using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for capturing screenshots of the Unity Editor windows (Scene view, Game view, or full editor).
    /// Uses Unity APIs when possible, falls back to Windows API (PrintWindow) for full editor capture.
    /// </summary>
    public class TakeScreenshotTool : McpToolBase
    {
        private const int MAX_WIDTH = 1920;
        private const int MAX_HEIGHT = 1080;

        public TakeScreenshotTool()
        {
            Name = "take_screenshot";
            Description = "Takes a screenshot of a Unity Editor window (scene, game, or full editor). " +
                          "Returns base64-encoded PNG image data.";
        }

        public override JObject Execute(JObject parameters)
        {
            string target = parameters["target"]?.ToObject<string>()?.ToLowerInvariant() ?? "game";
            int maxWidth = parameters["maxWidth"]?.ToObject<int>() ?? MAX_WIDTH;
            int maxHeight = parameters["maxHeight"]?.ToObject<int>() ?? MAX_HEIGHT;

            maxWidth = Mathf.Clamp(maxWidth, 64, MAX_WIDTH);
            maxHeight = Mathf.Clamp(maxHeight, 64, MAX_HEIGHT);

            try
            {
                byte[] pngBytes;
                string description;

                switch (target)
                {
                    case "scene":
                        (pngBytes, description) = CaptureSceneView(maxWidth, maxHeight);
                        break;
                    case "game":
                        (pngBytes, description) = CaptureGameView(maxWidth, maxHeight);
                        break;
                    case "editor":
                        (pngBytes, description) = CaptureEditorWindow(maxWidth, maxHeight);
                        break;
                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Invalid target '{target}'. Must be 'scene', 'game', or 'editor'.",
                            "validation_error"
                        );
                }

                if (pngBytes == null || pngBytes.Length == 0)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to capture {target} view — no image data produced.",
                        "capture_error"
                    );
                }

                string base64 = Convert.ToBase64String(pngBytes);

                McpLogger.LogInfo($"Screenshot captured: {target} view, {pngBytes.Length} bytes");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "image",
                    ["message"] = description,
                    ["imageData"] = base64,
                    ["mimeType"] = "image/png",
                    ["width"] = maxWidth,
                    ["height"] = maxHeight
                };
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Screenshot capture failed: {ex.Message}\n{ex.StackTrace}");
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error capturing screenshot: {ex.Message}",
                    "capture_error"
                );
            }
        }

        private (byte[], string) CaptureSceneView(int maxWidth, int maxHeight)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                throw new InvalidOperationException("No active Scene View found. Open a Scene View first.");
            }

            sceneView.Focus();
            sceneView.Repaint();

            var bytes = CaptureEditorWindowPixels(sceneView, maxWidth, maxHeight);
            string desc = $"Scene View screenshot ({sceneView.position.width}x{sceneView.position.height})";
            return (bytes, desc);
        }

        private (byte[], string) CaptureGameView(int maxWidth, int maxHeight)
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                throw new InvalidOperationException("Could not find GameView type.");
            }

            var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
            if (gameView == null)
            {
                throw new InvalidOperationException("No Game View found. Open a Game View first.");
            }

            gameView.Focus();
            gameView.Repaint();

            var bytes = CaptureEditorWindowPixels(gameView, maxWidth, maxHeight);
            string desc = $"Game View screenshot ({gameView.position.width}x{gameView.position.height})";
            return (bytes, desc);
        }

        private (byte[], string) CaptureEditorWindow(int maxWidth, int maxHeight)
        {
#if UNITY_EDITOR_WIN
            var bytes = CaptureMainEditorWindowWin32(maxWidth, maxHeight);
            if (bytes != null && bytes.Length > 0)
            {
                return (bytes, "Full Unity Editor window screenshot (Windows API)");
            }
#endif
            // Fallback: capture the focused editor window
            var focused = EditorWindow.focusedWindow;
            if (focused == null)
            {
                throw new InvalidOperationException("No focused editor window and Windows API capture failed.");
            }

            var fallbackBytes = CaptureEditorWindowPixels(focused, maxWidth, maxHeight);
            return (fallbackBytes, $"Focused editor window screenshot: {focused.titleContent.text}");
        }

        /// <summary>
        /// Captures an EditorWindow by reading its pixels via InternalEditorUtility.ReadScreenPixel.
        /// </summary>
        private byte[] CaptureEditorWindowPixels(EditorWindow window, int maxWidth, int maxHeight)
        {
            var pos = window.position;
            int srcW = (int)pos.width;
            int srcH = (int)pos.height;

            if (srcW <= 0 || srcH <= 0)
            {
                throw new InvalidOperationException($"Window '{window.titleContent.text}' has invalid size {srcW}x{srcH}.");
            }

            // Read pixels from screen at the window's position
            var pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
                new Vector2(pos.x, pos.y), srcW, srcH);

            var tex = new Texture2D(srcW, srcH, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply();

            // Downscale if needed
            if (srcW > maxWidth || srcH > maxHeight)
            {
                float scale = Mathf.Min((float)maxWidth / srcW, (float)maxHeight / srcH);
                int newW = Mathf.Max(1, (int)(srcW * scale));
                int newH = Mathf.Max(1, (int)(srcH * scale));
                tex = ScaleTexture(tex, newW, newH);
            }

            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return pngBytes;
        }

        /// <summary>
        /// Bilinear downscale of a Texture2D.
        /// </summary>
        private Texture2D ScaleTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            Graphics.Blit(source, rt);

            var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            result.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(source);

            return result;
        }

#if UNITY_EDITOR_WIN
        // ─── Windows API fallback for full editor window capture ───

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
            byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            // bmiColors is unused for 32-bit
        }

        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const uint DIB_RGB_COLORS = 0;
        private const uint BI_RGB = 0;

        /// <summary>
        /// Captures the main Unity Editor window using Windows PrintWindow API.
        /// </summary>
        private byte[] CaptureMainEditorWindowWin32(int maxWidth, int maxHeight)
        {
            IntPtr hWnd = GetActiveWindow();
            if (hWnd == IntPtr.Zero)
            {
                McpLogger.LogWarning("Win32: GetActiveWindow returned null.");
                return null;
            }

            if (!GetWindowRect(hWnd, out RECT rect))
            {
                McpLogger.LogWarning("Win32: GetWindowRect failed.");
                return null;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
            {
                McpLogger.LogWarning($"Win32: Invalid window rect {width}x{height}.");
                return null;
            }

            IntPtr hdcWindow = GetDC(hWnd);
            IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcWindow, width, height);
            IntPtr hOld = SelectObject(hdcMem, hBitmap);

            // PW_RENDERFULLCONTENT captures even DWM-composed content
            bool captured = PrintWindow(hWnd, hdcMem, PW_RENDERFULLCONTENT);

            if (!captured)
            {
                McpLogger.LogWarning("Win32: PrintWindow failed, window may be minimized.");
                SelectObject(hdcMem, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(hWnd, hdcWindow);
                return null;
            }

            // Read pixels via GetDIBits
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            byte[] pixelData = new byte[width * height * 4];
            GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmi, DIB_RGB_COLORS);

            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(hWnd, hdcWindow);

            // Convert BGRA → RGBA and create Texture2D
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var colors = new Color32[width * height];
            for (int i = 0; i < colors.Length; i++)
            {
                int offset = i * 4;
                colors[i] = new Color32(
                    pixelData[offset + 2], // R (was B)
                    pixelData[offset + 1], // G
                    pixelData[offset + 0], // B (was R)
                    255                     // A
                );
            }
            tex.SetPixels32(colors);
            tex.Apply();

            // Downscale if needed
            if (width > maxWidth || height > maxHeight)
            {
                float scale = Mathf.Min((float)maxWidth / width, (float)maxHeight / height);
                int newW = Mathf.Max(1, (int)(width * scale));
                int newH = Mathf.Max(1, (int)(height * scale));
                tex = ScaleTexture(tex, newW, newH);
            }

            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return pngBytes;
        }
#endif
    }
}
