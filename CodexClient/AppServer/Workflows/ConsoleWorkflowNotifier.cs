using System.Runtime.InteropServices;

namespace CodexClient.AppServer.Workflows;

/// <summary>僅對可見的 Windows Console 視窗閃爍工作列，不發出聲音。</summary>
internal static class ConsoleWorkflowNotifier
{
    private const uint FlashTaskbar = 0x00000002;
    private const uint FlashUntilForeground = 0x0000000C;
    internal static void Notify()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected || !Environment.UserInteractive)
        {
            return;
        }

        try
        {
            IntPtr window = GetConsoleWindow();
            // Pseudoconsole 的隱藏視窗不是使用者看到的 Terminal 視窗。
            if (window == IntPtr.Zero || !IsWindowVisible(window) || GetForegroundWindow() == window)
            {
                return;
            }

            var flash = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = window,
                Flags = FlashTaskbar | FlashUntilForeground,
                Count = uint.MaxValue,
                Timeout = 0
            };
            // 回傳值代表呼叫前的視窗狀態，不是操作成功與否。
            FlashWindowEx(ref flash);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            // 通知不可中斷問答或取代原本的執行結果。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}
