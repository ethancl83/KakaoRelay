using System.IO;
using System.Windows;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public partial class App : Application
{
    private Mutex? instanceGate;
    private bool ownsGate;
    private RelayApi? api;
    private LocalChatReader? chatReader;
    private CliAiRunner? aiRunner;
    private TrayHost? tray;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showRegistration;
    private bool exiting, allowClose, startingApi;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--send-image")
        {
            try
            {
                var command = System.Text.Json.JsonSerializer.Deserialize<ApiSendCommand>(File.ReadAllText(e.Args[1]), ReportStore.JsonOptions)
                    ?? throw new InvalidDataException("이미지 요청이 없습니다.");
                if (!command.IsImage) throw new InvalidDataException("이미지 요청만 지원합니다.");
                var sender = new ApiSendService(TestSender.DefaultLedger, ConversationCatalog.Scan, () => new KakaoTestTransport(), () => new KakaoImageTransport());
                var receipt = await sender.SendAsync(command);
                await File.WriteAllTextAsync(e.Args[2], System.Text.Json.JsonSerializer.Serialize(receipt, ReportStore.JsonOptions));
                Shutdown(receipt.EnterPosted && receipt.InputCleared == true ? 0 : 2);
            }
            catch { Shutdown(2); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--worker")
        {
            if (e.Args.Length != 2) { Shutdown(64); return; }
            try
            {
                var report = await Task.Run(() => DiagnosticCollector.Collect(e.Args[1]));
                Shutdown(report.Status == "error" ? 2 : 0);
            }
            catch { Shutdown(2); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--diagnose")
        {
            if (e.Args.Length != 2) { Shutdown(64); return; }
            try
            {
                var report = await ProbeRunner.RunAsync(Environment.ProcessPath!, e.Args[1], TimeSpan.FromSeconds(20));
                Shutdown(report.Status is "complete" or "partial" ? 0 : 2);
            }
            catch { Shutdown(2); }
            return;
        }
        if (e.Args.Length > 0 && !(e.Args.Length == 2 && e.Args[0] == "--report")) { Shutdown(64); return; }
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KakaoRelay.ShowWindow");
        instanceGate = new Mutex(false, @"Local\KakaoRelay.MainWindow");
        try { ownsGate = instanceGate.WaitOne(0); } catch (AbandonedMutexException) { ownsGate = true; }
        if (!ownsGate)
        {
            showSignal.Set();
            Shutdown(); return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var main = new MainWindow();
        MainWindow = main;
        tray = new TrayHost(ShowMainWindow, () => _ = ExitFromTrayAsync(main));
        main.Closing += (_, args) => { if (!allowClose) { args.Cancel = true; main.Hide(); } };
        SessionEnding += (_, _) => { allowClose = true; };
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ShowMainWindow); },
            null, Timeout.Infinite, false);
        main.Show();
        if (e.Args.Length == 2) main.LoadReport(Path.GetFullPath(e.Args[1]));
        startingApi = true;
        try
        {
            var sends = new ApiSendService(TestSender.DefaultLedger, ConversationCatalog.Scan, () => new KakaoTestTransport(), () => new KakaoImageTransport());
            chatReader = new LocalChatReader();
            aiRunner = new CliAiRunner();
            var ai = new AiService(chatReader, new AiSettingsStore(AiSettingsStore.DefaultPath), aiRunner);
            Task<TestSendReceipt> Send(ApiSendCommand command) => main.Dispatcher.InvokeAsync(() => main.SendFromApiAsync(() => sends.SendAsync(command))).Task.Unwrap();
            main.InitializeAi(ai, Send);
            api = await RelayApi.StartAsync(RelayApi.DefaultConnectionPath, ConversationCatalog.Scan,
                () => TestSender.ReadHistory(TestSender.DefaultLedger),
                Send, ai);
            main.SetApiStatus("AI API 연결됨 · 로컬 게이트웨이 실행 중");
        }
        catch { main.SetApiStatus("AI API 시작 실패 · 앱을 다시 실행하세요."); }
        finally { startingApi = false; }
    }
    private void ShowMainWindow()
    {
        if (exiting || MainWindow is not { } main) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        if (main is MainWindow relayWindow) relayWindow.RestoreFixedSize();
        main.Activate();
    }
    private async Task ExitFromTrayAsync(MainWindow main)
    {
        if (exiting) return;
        exiting = true;
        main.PrepareShutdown();
        tray?.SetExiting();
        // Stop admitting work, then let any active send persist its final receipt.
        while (startingApi || main.Working) await Task.Delay(100);
        if (api is { } running)
        {
            api = null;
            try { await running.DisposeAsync(); } catch { }
        }
        allowClose = true;
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        showRegistration?.Unregister(null);
        showSignal?.Dispose();
        tray?.Dispose();
        if (api is not null)
        {
            try { Task.Run(async () => await api.DisposeAsync()).GetAwaiter().GetResult(); }
            catch { }
        }
        if (ownsGate) instanceGate?.ReleaseMutex();
        chatReader?.Dispose();
        aiRunner?.Dispose();
        instanceGate?.Dispose();
        base.OnExit(e);
    }
}
