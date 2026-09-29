using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KakaoRelay.Core;

public static class ProviderStorage
{
    private static readonly object Sync = new();
    public static string Root => Path.Combine(AppContext.BaseDirectory, "data", "providers");
    public static string Home(string provider)
    {
        if (!AiSettings.Order.Contains(provider)) throw new ArgumentException("지원하지 않는 프로바이더입니다.");
        return Path.Combine(Root, provider);
    }
    private static string LegacyHome(string provider)
    {
        var variable = provider switch { "codex" => "CODEX_HOME", "grok" => "GROK_HOME", _ => "CLAUDE_CONFIG_DIR" };
        var configured = Environment.GetEnvironmentVariable(variable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + provider) : configured);
    }
    public static void Initialize()
    {
        foreach (var provider in AiSettings.Order) Ensure(provider);
    }
    private static void Ensure(string provider)
    {
        lock (Sync)
        {
            var home = Home(provider);
            Directory.CreateDirectory(home);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var marker = Path.Combine(home, ".initialized-v1");
            if (File.Exists(marker)) return;
            var authFile = provider == "claude" ? ".credentials.json" : "auth.json";
            CopyFile(Path.Combine(LegacyHome(provider), authFile), Path.Combine(home, authFile));
            // Personal hooks, plugins, endpoint overrides and state DB paths are not inherited.
            if (provider == "codex" && !File.Exists(Path.Combine(home, "config.toml")))
                File.WriteAllText(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
            if (provider == "grok" && !File.Exists(Path.Combine(home, "config.toml")))
                File.WriteAllText(Path.Combine(home, "config.toml"), "[cli]\nauto_update = false\n");
            File.WriteAllText(marker, "KakaoRelay provider storage v1\n");
        }
    }
    public static string JobFolder(string provider, string kind)
    {
        Ensure(provider);
        var path = Path.Combine(Home(provider), "jobs", kind + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static string ClaudeProject(string cwd) => "kakaorelay-" + AiConversation.Hash(Path.GetFullPath(cwd));
    public static void Configure(ProcessStartInfo info, string provider)
    {
        Ensure(provider);
        var home = Home(provider);
        // Set only the child environment; never change the user's desktop/CLI environment.
        foreach (var id in AiSettings.Order)
            info.Environment[id switch { "codex" => "CODEX_HOME", "grok" => "GROK_HOME", _ => "CLAUDE_CONFIG_DIR" }] = Home(id);
        info.Environment["CODEX_SQLITE_HOME"] = Home("codex");
        if (provider == "codex")
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sqlite_home=" + JsonSerializer.Serialize(home));
        }
        if (provider == "claude")
        {
            info.Environment["CLAUDE_CODE_PROJECT_DIR_NAME"] = ClaudeProject(info.WorkingDirectory);
            info.Environment["CLAUDE_CODE_TMPDIR"] = Path.Combine(home, "tmp");
            Directory.CreateDirectory(info.Environment["CLAUDE_CODE_TMPDIR"]!);
        }
    }
    public static string ConversationFolder(string provider, string identity)
    {
        Ensure(provider);
        var folder = Path.Combine(Home(provider), "conversations", identity);
        lock (Sync)
        {
            var statePath = Path.Combine(folder, "state.json");
            if (File.Exists(statePath)) return folder;
            var oldFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KakaoRelay", "ai-sessions", identity);
            if (!File.Exists(Path.Combine(oldFolder, "state.json"))) return folder;
            var state = AiConversation.Load(oldFolder);
            var imported = false;
            if (Guid.TryParse(state.SessionId, out _))
            {
                var source = LegacyHome(provider);
                if (provider == "codex")
                {
                    foreach (var category in new[] { "sessions", "archived_sessions" })
                    {
                        var logs = Path.Combine(source, category);
                        if (!Directory.Exists(logs)) continue;
                        foreach (var file in Directory.EnumerateFiles(logs, $"*{state.SessionId}.jsonl", SearchOption.AllDirectories))
                        {
                            // The known session ID is owned by this app's room state.
                            CopyFile(file, Path.Combine(Home(provider), "sessions", Path.GetRelativePath(logs, file)));
                            imported = true;
                        }
                    }
                }
                else if (provider == "grok")
                {
                    var oldProject = Path.Combine(source, "sessions", Uri.EscapeDataString(oldFolder));
                    var session = Path.Combine(oldProject, state.SessionId!);
                    if (Directory.Exists(session))
                    {
                        CopyTree(session, Path.Combine(Home(provider), "sessions", Uri.EscapeDataString(folder), state.SessionId!));
                        imported = true;
                    }
                }
                else
                {
                    var oldProject = Path.Combine(source, "projects", Regex.Replace(oldFolder, "[^a-zA-Z0-9]", "-"));
                    var transcript = Path.Combine(oldProject, state.SessionId + ".jsonl");
                    if (File.Exists(transcript))
                    {
                        var destination = Path.Combine(Home(provider), "projects", ClaudeProject(folder));
                        CopyFile(transcript, Path.Combine(destination, state.SessionId + ".jsonl"));
                        CopyTree(Path.Combine(oldProject, state.SessionId!), Path.Combine(destination, state.SessionId!));
                        imported = true;
                    }
                }
            }
            // Never claim a resumed context when its native transcript could not be imported.
            (imported ? state : new AiConversation()).Save(folder);
        }
        return folder;
    }
    private static void CopyFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}
