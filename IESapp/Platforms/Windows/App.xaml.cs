using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace IESapp.WinUI
{
    public partial class App : MauiWinUIApplication
    {
        private const string AppMutexName = "IESapp_SingleInstance_Mutex";
        private const string AppPipeName  = "IESapp_OAuth_Pipe";

        private static Mutex? _singleInstanceMutex;
        private static CancellationTokenSource? _pipeListenerCts;

        public App()
        {
            // WinUI must be initialized first — even for the early-exit (second-instance) path
            this.InitializeComponent();

            // ── Single-instance gate ─────────────────────────────────────────
            _singleInstanceMutex = new Mutex(true, AppMutexName, out bool isFirstInstance);

            var args         = Environment.GetCommandLineArgs();
            bool hasOAuthUri = args.Length >= 2 &&
                               args[1].StartsWith("iesapp://", StringComparison.OrdinalIgnoreCase);

            if (!isFirstInstance)
            {
                // Second instance: forward the URI to the first instance, then quit
                if (hasOAuthUri)
                    SendUriToFirstInstance(args[1]);

                Environment.Exit(0);
                return;         // never reached, keeps compiler happy
            }

            // ── First instance ───────────────────────────────────────────────
            // Register iesapp:// in HKCU so Windows can redirect OAuth callbacks here
            RegisterUriScheme();

            // Start the pipe server so subsequent instances can forward URIs to us
            StartPipeListener();

            // Edge-case: the very first launch was via iesapp:// (unusual but handled)
            if (hasOAuthUri)
            {
                Task.Delay(800).ContinueWith(_ =>
                    IESapp.Services.SupabaseService.HandleOAuthCallback(new Uri(args[1])));
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        // ── Named Pipe: second instance → first instance ─────────────────────

        private static void SendUriToFirstInstance(string uri)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", AppPipeName,
                    PipeDirection.Out, PipeOptions.None);
                pipe.Connect(3000);
                using var writer = new StreamWriter(pipe);
                writer.WriteLine(uri);
                writer.Flush();
                Console.WriteLine($"[SingleInstance] Forwarded URI to first instance: {uri}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SingleInstance] Pipe send failed: {ex.Message}");
            }
        }

        private static void StartPipeListener()
        {
            _pipeListenerCts = new CancellationTokenSource();
            var token = _pipeListenerCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(
                            AppPipeName,
                            PipeDirection.In,
                            maxNumberOfServerInstances: 1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await pipe.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(pipe);
                        var uri = await reader.ReadLineAsync(token);

                        if (!string.IsNullOrEmpty(uri) && uri.StartsWith("iesapp://"))
                        {
                            Console.WriteLine($"[SingleInstance] Received OAuth URI: {uri}");
                            IESapp.Services.SupabaseService.HandleOAuthCallback(new Uri(uri));
                            // Bring the first instance window to the front
                            BringToForeground();
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SingleInstance] Pipe listener error: {ex.Message}");
                        await Task.Delay(500, token);
                    }
                }
            }, token);
        }

        // ── Win32: bring window to foreground ────────────────────────────────

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        private const int SW_RESTORE = 9;

        private static void BringToForeground()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var hwnd    = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) return;

                if (IsIconic(hwnd))          // if minimized, restore first
                    ShowWindow(hwnd, SW_RESTORE);

                SetForegroundWindow(hwnd);
                Console.WriteLine("[Foreground] App brought to front.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Foreground] Failed: {ex.Message}");
            }
        }

        // ── HKCU URI scheme self-registration ────────────────────────────────

        private static void RegisterUriScheme()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath)) return;

                const string schemeName = "iesapp";
                const string hkcuBase   = @"SOFTWARE\Classes\";

                using var schemeKey = Registry.CurrentUser.CreateSubKey($@"{hkcuBase}{schemeName}");
                schemeKey.SetValue("",             $"URL:{schemeName} Protocol");
                schemeKey.SetValue("URL Protocol", "");

                using var cmdKey = Registry.CurrentUser.CreateSubKey(
                    $@"{hkcuBase}{schemeName}\shell\open\command");
                cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");

                Console.WriteLine($"[URIScheme] Registered {schemeName}:// → {exePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[URIScheme] Registration failed: {ex.Message}");
            }
        }
    }
}
