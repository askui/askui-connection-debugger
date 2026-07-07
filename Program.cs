// AskUI Connection Debugger
// Diagnoses DNS, TCP, proxy, and gRPC connectivity to an AskUI Controller.
// Usage: askui-debug [--port 26000] [--verbose] [--no-color] <host>

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

const string Version = "0.2.0";

Console.OutputEncoding = Encoding.UTF8;

// ─── Argument parsing / interactive mode ──────────────────────────────────────

string? host           = null;
int     port           = 26000;
bool    verbose        = false;
bool    noColor        = false;
string? outputFilename = null;
StreamWriter? outputFile = null;

// Always keep a reference to the real screen before any redirection.
TextWriter screenOut = Console.Out;

if (args.Length == 0)
{
    // Interactive mode: no arguments — user probably double-clicked the binary.
    Console.WriteLine();
    Console.WriteLine("  AskUI Connection Debugger v" + Version);
    Console.WriteLine();
    Console.Write("  Hostname or IP address: ");
    host = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(host))
    {
        Console.Error.WriteLine("  No host entered. Press Enter to exit.");
        Console.ReadLine();
        return 1;
    }

    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    var defaultPath = Path.Combine(desktop, "askui-debug-output.txt");
    Console.Write($"  Output file [{defaultPath}]: ");
    var fn = Console.ReadLine()?.Trim();
    outputFilename = string.IsNullOrEmpty(fn)
        ? defaultPath
        : Path.IsPathRooted(fn) ? fn : Path.Combine(desktop, fn);

    outputFile = new StreamWriter(outputFilename, append: false, Encoding.UTF8);
    noColor = true; // file output must be plain text
}
else
{
    // CLI mode: parse flags.
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--port" or "-p" when i + 1 < args.Length:
                port = int.Parse(args[++i]);
                break;
            case "--verbose" or "-v":
                verbose = true;
                break;
            case "--no-color":
                noColor = true;
                break;
            case "--help" or "-h":
                PrintUsage();
                return 0;
            default:
                if (!args[i].StartsWith('-'))
                    host = args[i];
                break;
        }
    }

    if (host is null) { PrintUsage(); return 1; }
}

// Normalize: strip scheme / trailing slash
host = host.TrimStart()
           .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
           .Replace("http://",  "", StringComparison.OrdinalIgnoreCase)
           .TrimEnd('/');

// Handle host:port shorthand (e.g. "10.150.122.214:26000")
if (!host.StartsWith('[') && host.Contains(':'))
{
    var lastColon = host.LastIndexOf(':');
    if (int.TryParse(host[(lastColon + 1)..], out var embeddedPort))
    {
        port = embeddedPort;
        host = host[..lastColon];
    }
}

// ─── Wire up logging and progress display ────────────────────────────────────
//
// Interactive mode:
//   • Logger   → outputFile only  (full details, no screen noise)
//   • Progress → screenOut only   (spinner/step list, no file noise)
//   • Summary  → TeeWriter        (results appear on screen AND in file)
//
// CLI mode:
//   • Logger   → Console.Out      (full verbose output, current behaviour)
//   • Progress → disabled         (Logger already shows everything)
//   • Summary  → Console.Out

Logger log;
TerminalProgress progress;
TextWriter summaryOut;

if (outputFile is not null)
{
    log        = new Logger(verbose, false, outputFile);
    progress   = new TerminalProgress(screenOut, enabled: true);
    summaryOut = new TeeWriter(screenOut, outputFile);
}
else
{
    log        = new Logger(verbose, !noColor, Console.Out);
    progress   = new TerminalProgress(Console.Out, enabled: false);
    summaryOut = Console.Out;
}

// Header — written via summaryOut so it lands in both screen and file.
summaryOut.WriteLine();
summaryOut.WriteLine($"  AskUI Connection Debugger v{Version}");
summaryOut.WriteLine($"  Target: {host}:{port}");
summaryOut.WriteLine();
summaryOut.Flush();

var checker = new Checker(log, progress, summaryOut, host, port);
await checker.RunAllAsync();

if (outputFile is not null)
{
    summaryOut.WriteLine();
    summaryOut.WriteLine($"  Results saved to: {Path.GetFullPath(outputFilename!)}");
    summaryOut.WriteLine("  Send that file to the AskUI team.");
    summaryOut.WriteLine();
    summaryOut.WriteLine("  You can close this window now.");
    summaryOut.Flush();
    await outputFile.FlushAsync();
    outputFile.Dispose();
}

return 0;

// ─── Usage ────────────────────────────────────────────────────────────────────

static void PrintUsage() => Console.Error.WriteLine("""
    AskUI Connection Debugger

    Diagnoses DNS, TCP, proxy, and gRPC connectivity to an AskUI Controller.

    Usage:
      askui-debug [flags] <host>

    Flags:
      --port, -p <port>   Target port (default: 26000)
      --verbose, -v       Show debug-level output
      --no-color          Disable colored output
      --help, -h          Show this help

    Examples:
      askui-debug Winmod-TS4
      askui-debug --port 6769 10.150.122.214
      askui-debug --verbose --no-color 10.150.122.214
    """);


// ═════════════════════════════════════════════════════════════════════════════
// Logger — writes detailed output to a TextWriter (file or console).
// ═════════════════════════════════════════════════════════════════════════════

class Logger
{
    private readonly bool       _verbose;
    private readonly bool       _color;
    private readonly TextWriter _out;

    // ANSI escape codes
    private const string Reset  = "\x1b[0m";
    private const string Bold   = "\x1b[1m";
    private const string Dim    = "\x1b[2m";
    private const string Red    = "\x1b[31m";
    private const string Green  = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Cyan   = "\x1b[36m";

    public Logger(bool verbose, bool color, TextWriter output)
    {
        _verbose = verbose;
        _out     = output;
        if (color && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { EnableWindowsAnsi(); } catch { color = false; }
        }
        _color = color;
    }

    public void Section(string name)
    {
        const int w = 56;
        var bar = new string('─', w);
        _out.WriteLine();
        _out.WriteLine("  " + C(Cyan, bar));
        _out.WriteLine("  " + C(Bold, "  " + name));
        _out.WriteLine("  " + C(Cyan, bar));
    }

    public void Pass (string fmt, params object?[] a) => Line(C(Green,  "[PASS]"), fmt, a);
    public void Fail (string fmt, params object?[] a) => Line(C(Red,    "[FAIL]"), fmt, a);
    public void Warn (string fmt, params object?[] a) => Line(C(Yellow, "[WARN]"), fmt, a);
    public void Info (string fmt, params object?[] a) => Line(C(Cyan,   "[INFO]"), fmt, a);
    public void Sub  (string fmt, params object?[] a) => _out.WriteLine("         " + string.Format(fmt, a));

    public void Debug(string fmt, params object?[] a)
    {
        if (_verbose) Line(C(Dim, "[DBUG]"), fmt, a);
    }

    private void Line(string tag, string fmt, object?[] a) =>
        _out.WriteLine($"  {tag} {string.Format(fmt, a)}");

    private string C(string code, string text) =>
        _color ? $"{code}{text}{Reset}" : text;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnableWindowsAnsi()
    {
        var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        GetConsoleMode(handle, out var mode);
        SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);
}


// ═════════════════════════════════════════════════════════════════════════════
// TerminalProgress — spinner + step list displayed on the screen.
// Disabled in CLI mode (Logger already writes everything).
// ═════════════════════════════════════════════════════════════════════════════

class TerminalProgress
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private const string PassIcon = "✓";
    private const string FailIcon = "✗";

    private readonly TextWriter _screen;
    private readonly bool       _enabled;
    private CancellationTokenSource? _cts;
    private Task?   _spinnerTask;
    private string  _stepName = "";

    public TerminalProgress(TextWriter screen, bool enabled)
    {
        _screen  = screen;
        _enabled = enabled;
    }

    public void Begin(string stepName)
    {
        if (!_enabled) return;
        _stepName = stepName;
        _cts = new CancellationTokenSource();
        _spinnerTask = SpinAsync(_cts.Token);
    }

    public async Task EndAsync(bool passed)
    {
        if (!_enabled) return;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try { await _spinnerTask!; } catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
        }

        var icon = passed ? PassIcon : FailIcon;
        var line = $"  {icon} {_stepName}";
        var width = 0;
        try { width = Console.WindowWidth - 1; } catch { width = 79; }
        // Overwrite the spinner line, pad to erase any leftover characters.
        _screen.Write('\r');
        _screen.WriteLine(line.PadRight(Math.Max(line.Length, width)));
    }

    private async Task SpinAsync(CancellationToken ct)
    {
        int frame = 0;
        while (!ct.IsCancellationRequested)
        {
            var spinner = Frames[frame % Frames.Length];
            _screen.Write($"\r  {spinner} {_stepName}...");
            frame++;
            try { await Task.Delay(80, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}


// ═════════════════════════════════════════════════════════════════════════════
// Check result tracking
// ═════════════════════════════════════════════════════════════════════════════

record CheckResult(string Name, bool Passed, string Note);
record FirewallRule(string Name, string Action, string Profiles);


// ═════════════════════════════════════════════════════════════════════════════
// Checker — orchestrates all checks
// ═════════════════════════════════════════════════════════════════════════════

class Checker(Logger log, TerminalProgress progress, TextWriter summaryOut, string host, int port)
{
    private readonly List<CheckResult> _results    = [];
    private readonly List<string>      _resolvedIPs = [];

    private string TargetUrl => $"http://{host}:{port}";

    public async Task RunAllAsync()
    {
        await RunAsync("System Information",         () => { CheckSystemInfo();      return Task.CompletedTask; });
        await RunAsync("Local AskUI Service",        CheckLocalServiceAsync);
        await RunAsync("Local Network Interfaces",   () => { CheckLocalInterfaces(); return Task.CompletedTask; });
        await RunAsync("DNS / Name Resolution",      CheckDnsAsync);
        await RunAsync("Proxy Configuration",        () => { CheckProxy();           return Task.CompletedTask; });
        await RunAsync("Windows Firewall Rules",     CheckFirewallAsync);
        await RunAsync("TCP Connectivity",           CheckTcpAsync);
        await RunAsync("gRPC — no proxy",            () => CheckGrpcAsync(useProxy: false));
        await RunAsync("gRPC — system proxy",        () => CheckGrpcAsync(useProxy: true));
        await RunAsync("Controller Discovery",       CheckAskUIControllerAsync);
        PrintSummary();
    }

    // Wraps a check: starts the spinner, runs the check, stops the spinner.
    // Pass/fail for the spinner is determined by whether any new failure results
    // were added to _results during the check.
    private async Task RunAsync(string name, Func<Task> check)
    {
        log.Section(name);
        progress.Begin(name);
        var failsBefore = _results.Count(r => !r.Passed);
        await check();
        var failsAfter = _results.Count(r => !r.Passed);
        await progress.EndAsync(failsAfter == failsBefore);
    }

    // ── Record helpers ────────────────────────────────────────────────────────

    private void Pass(string name, string note)
    {
        _results.Add(new(name, true, note));
        log.Pass("{0}", note);
    }

    private void Fail(string name, string note)
    {
        _results.Add(new(name, false, note));
        log.Fail("{0}", note);
    }

    // ── Check: System Information ─────────────────────────────────────────────

    private void CheckSystemInfo()
    {
        log.Info("Version    : {0}", "0.2.0");
        log.Info("OS / Arch  : {0} / {1}", RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture);
        log.Info("Hostname   : {0}", Dns.GetHostName());
        log.Info("Time (UTC) : {0}", DateTime.UtcNow.ToString("o"));
        log.Info("Target     : {0}:{1}", host, port);
    }

    // ── Check: Local AskUI Service ────────────────────────────────────────────
    //
    // Checks whether the AskUI Core Service (or a standalone Controller) is
    // listening on port 26000 on THIS machine — the one running the tool.
    // Useful when the tool is run on the controller machine itself to verify
    // the service started successfully.

    private async Task CheckLocalServiceAsync()
    {
        log.Info("Checking whether port 26000 is listening locally on this machine...");

        // Use the TCP listener table — faster and more reliable than a connect.
        bool listening;
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            listening = listeners.Any(ep => ep.Port == 26000);
        }
        catch
        {
            // Fallback: try a TCP connect if the listener table isn't available.
            using var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync("127.0.0.1", 26000).WaitAsync(TimeSpan.FromSeconds(2));
                listening = true;
            }
            catch { listening = false; }
        }

        if (listening)
        {
            Pass("Local Service", "AskUI Core Service is listening on port 26000 locally");
        }
        else
        {
            log.Info("Port 26000 is not listening on this machine.");
            log.Sub("This is expected if the tool is run on the CLIENT machine");
            log.Sub("(the controller runs on the remote machine, not here).");
            log.Sub("If this IS the controller machine, start the AskUI Core Service.");
            // Not recorded as a failure — the client machine is not expected to run the service.
        }
    }

    // ── Check: Local Network Interfaces ───────────────────────────────────────

    private void CheckLocalInterfaces()
    {
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

            var up    = iface.OperationalStatus == OperationalStatus.Up;
            var state = up ? "UP  " : "DOWN";
            var ipProps = iface.GetIPProperties();
            var ips = new List<string>();
            var hasApipa = false;

            foreach (var addr in ipProps.UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ip.ToString();
                if (IsApipa(ip)) { s += "  ⚠ APIPA — DHCP failed, not routable remotely"; hasApipa = true; }
                ips.Add(s);
            }

            var ipStr = ips.Count > 0 ? string.Join(" | ", ips) : "(no IPv4 address)";

            if      (hasApipa) log.Warn("[{0}] {1,-22} {2}", state, iface.Name, ipStr);
            else if (up)       log.Pass("[{0}] {1,-22} {2}", state, iface.Name, ipStr);
            else               log.Info("[{0}] {1,-22} {2}", state, iface.Name, ipStr);
        }
    }

    // ── Check: DNS / Name Resolution ──────────────────────────────────────────

    private async Task CheckDnsAsync()
    {
        if (IPAddress.TryParse(host, out _))
        {
            log.Info("Host is a raw IP address — DNS resolution skipped");
            _resolvedIPs.Add(host);
            _results.Add(new("DNS", true, "raw IP, no resolution needed"));
            return;
        }

        log.Info("Resolving \"{0}\" via system resolver (hosts → DNS → LLMNR → NetBIOS)...", host);
        log.Debug("System resolver = Dns.GetHostAddressesAsync (calls getaddrinfo on all platforms)");

        try
        {
            var sw = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();

            log.Pass("Resolved {0} address(es) for \"{1}\" in {2}ms:", addresses.Length, host, sw.ElapsedMilliseconds);

            bool anyUsable = false;
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    log.Debug("  {0} (IPv6 — skipping primary display)", addr);
                    continue;
                }
                if (IsApipa(addr))
                {
                    log.Warn("  {0}  ← APIPA (169.254.x.x) — this adapter has no DHCP lease", addr);
                    log.Sub("    The machine has a working adapter on a different IP.");
                    log.Sub("    Run `ipconfig /all` on the target and look for 10.x / 192.168.x / 172.x");
                }
                else
                {
                    log.Sub("  {0}", addr);
                    _resolvedIPs.Add(addr.ToString());
                    anyUsable = true;
                }
            }

            if (!anyUsable)
            {
                Fail("DNS", "all resolved IPs are APIPA (169.254.x.x) — unreachable over the network");
                return;
            }
            _results.Add(new("DNS", true, $"resolved to {string.Join(", ", _resolvedIPs)}"));
        }
        catch (Exception ex)
        {
            Fail("DNS", $"resolution failed: {ex.Message}");
            log.Sub("→ Try the IP address directly instead of the hostname.");
            log.Sub("→ On the target machine, run `ipconfig /all` to find its real IP.");
            log.Debug("Full exception: {0}", ex);
        }
    }

    // ── Check: Proxy Configuration ────────────────────────────────────────────

    private void CheckProxy()
    {
        var envVars = new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "NO_PROXY", "no_proxy" };
        bool anySet = false;
        foreach (var key in envVars)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val is not null) { log.Warn("{0,-14} = {1}", key, val); anySet = true; }
            else                   log.Debug("{0,-14} = (not set)", key);
        }
        if (!anySet) log.Pass("No proxy environment variables set");

        var sysProxy = WebRequest.DefaultWebProxy;
        if (sysProxy is not null)
        {
            var targetUri = new Uri(TargetUrl);
            var proxyUri  = sysProxy.GetProxy(targetUri);
            var bypassed  = sysProxy.IsBypassed(targetUri);
            if (!bypassed && proxyUri?.Host != targetUri.Host)
            {
                log.Warn("System proxy detected for {0}: {1}", TargetUrl, proxyUri);
                log.Sub("gRPC over HTTP/2 cleartext (h2c) cannot tunnel through most HTTP proxies.");
                log.Sub("The proxy may silently drop or reject gRPC connections.");
            }
            else
            {
                log.Pass("No proxy would be used for {0} (direct connection)", TargetUrl);
            }
        }
        else
        {
            log.Pass("WebRequest.DefaultWebProxy is null — no system proxy configured");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            log.Info("Tip: Run `netsh winhttp show proxy` to inspect the WinHTTP proxy separately.");
        }
    }

    // ── Check: Windows Firewall Rules ─────────────────────────────────────────
    //
    // Reads the global firewall policy (on/off, default inbound/outbound actions)
    // and queries rules specific to the target port:
    //   • Inbound  — relevant if THIS machine is the AskUI Controller.
    //   • Outbound — relevant if THIS machine is the CLIENT connecting remotely.
    //
    // Only meaningful on Windows; other platforms get manual-command hints.

    private async Task CheckFirewallAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            log.Info("Windows Firewall check skipped — not running on Windows.");
            log.Info("macOS  : sudo pfctl -sr | grep {0}", port);
            log.Info("Linux  : sudo iptables -L -n | grep {0}  or  sudo nft list ruleset", port);
            return;
        }

        log.Info("Reading Windows Firewall profiles and port {0} rules...", port);

        // Global policy: firewall state + default inbound/outbound actions
        var profileOutput = await RunCommandAsync("netsh", "advfirewall show allprofiles");
        log.Debug("netsh show allprofiles output:{0}{1}", Environment.NewLine, profileOutput);
        ParseProfileOutput(profileOutput, out bool firewallEnabled, out bool outboundDefaultBlock);

        if (!firewallEnabled)
        {
            log.Pass("Windows Firewall is OFF on all active profiles — no rules apply.");
            Pass("Firewall:Outbound", "firewall off — no outbound blocks");
            Pass("Firewall:Inbound",  "firewall off — no inbound blocks");
            log.Info("Run this tool on the TARGET machine to check its firewall.");
            return;
        }

        log.Info("Windows Firewall is ON.");
        if (outboundDefaultBlock)
            log.Warn("Default outbound policy: BLOCK (restrictive corporate configuration).");
        else
            log.Pass("Default outbound policy: ALLOW — outbound traffic permitted by default.");

        // ── Inbound rules for TCP port <port> ─────────────────────────────────
        var inboundRules = await QueryPortRulesAsync(direction: "Inbound", localPort: true);
        var inAllows = inboundRules.Where(r => r.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase)).ToList();
        var inBlocks = inboundRules.Where(r => r.Action.Equals("Block", StringComparison.OrdinalIgnoreCase)).ToList();

        if (inBlocks.Count > 0)
        {
            Fail("Firewall:Inbound", $"TCP port {port} is explicitly BLOCKED inbound on this machine");
            foreach (var r in inBlocks)
                log.Sub("[BLOCK] \"{0}\" (Profiles: {1})", r.Name, r.Profiles);
            log.Sub("→ Remove the blocking rule, or add an allow rule (run as admin):");
            log.Sub("    netsh advfirewall firewall add rule name=\"AskUI Controller\" dir=in action=allow protocol=TCP localport={0}", port);
        }
        else if (inAllows.Count > 0)
        {
            Pass("Firewall:Inbound", $"{inAllows.Count} inbound allow rule(s) for TCP port {port}");
            foreach (var r in inAllows)
                log.Sub("[ALLOW] \"{0}\" (Profiles: {1})", r.Name, r.Profiles);
        }
        else
        {
            log.Info("No inbound rules for TCP port {0} on this machine (default policy: Block).", port);
            log.Sub("→ If THIS machine is the AskUI Controller, add an inbound allow rule (run as admin):");
            log.Sub("    netsh advfirewall firewall add rule name=\"AskUI Controller\" dir=in action=allow protocol=TCP localport={0}", port);
            log.Sub("  If this is the CLIENT machine, no inbound rule is needed here.");
        }

        // ── Outbound rules for TCP remoteport <port> ──────────────────────────
        var outboundRules = await QueryPortRulesAsync(direction: "Outbound", localPort: false);
        var outAllows = outboundRules.Where(r => r.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase)).ToList();
        var outBlocks = outboundRules.Where(r => r.Action.Equals("Block", StringComparison.OrdinalIgnoreCase)).ToList();

        if (outBlocks.Count > 0)
        {
            Fail("Firewall:Outbound", $"outbound TCP to port {port} is explicitly BLOCKED on this machine");
            foreach (var r in outBlocks)
                log.Sub("[BLOCK] \"{0}\" (Profiles: {1})", r.Name, r.Profiles);
            log.Sub("→ Remove the blocking rule, or add an allow rule (run as admin):");
            log.Sub("    netsh advfirewall firewall add rule name=\"AskUI Client\" dir=out action=allow protocol=TCP remoteport={0}", port);
        }
        else if (outboundDefaultBlock && outAllows.Count == 0)
        {
            Fail("Firewall:Outbound", $"default outbound policy is BLOCK and no allow rule exists for TCP port {port}");
            log.Sub("→ Add an outbound allow rule (run as admin):");
            log.Sub("    netsh advfirewall firewall add rule name=\"AskUI Client\" dir=out action=allow protocol=TCP remoteport={0}", port);
        }
        else if (outAllows.Count > 0)
        {
            Pass("Firewall:Outbound", $"explicit outbound allow rule(s) for TCP port {port}");
            foreach (var r in outAllows)
                log.Sub("[ALLOW] \"{0}\" (Profiles: {1})", r.Name, r.Profiles);
        }
        else
        {
            Pass("Firewall:Outbound", $"default outbound policy is ALLOW — no blocks for TCP port {port}");
        }

        log.Info("This check reads THIS machine's firewall only.");
        log.Info("→ Run the tool on the TARGET machine to check inbound rules there.");
    }

    // Queries enabled Windows Firewall rules matching a specific TCP port using
    // PowerShell Get-NetFirewallPortFilter / Get-NetFirewallRule.
    // direction: "Inbound" or "Outbound"
    // localPort: true  → filters on LocalPort  (inbound rules, this machine is the server)
    //            false → filters on RemotePort (outbound rules, this machine is the client)
    private async Task<List<FirewallRule>> QueryPortRulesAsync(string direction, bool localPort)
    {
        var portProp = localPort ? "LocalPort" : "RemotePort";
        var script   = $@"
$ErrorActionPreference = 'SilentlyContinue'
try {{
    $pf = Get-NetFirewallPortFilter -Protocol TCP 2>$null |
          Where-Object {{ $_.{portProp} -eq '{port}' }}
    if ($pf) {{
        foreach ($f in @($pf)) {{
            Get-NetFirewallRule -AssociatedNetFirewallPortFilter $f 2>$null |
                Where-Object {{ $_.Direction.ToString() -eq '{direction}' -and $_.Enabled.ToString() -eq 'True' }} |
                ForEach-Object {{ ""$($_.DisplayName)|$($_.Action.ToString())|$($_.Profile.ToString())"" }}
        }}
    }}
}} catch {{}}
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var output  = await RunCommandAsync("powershell", $"-NoProfile -NonInteractive -EncodedCommand {encoded}");
        log.Debug("Firewall {0} query:{1}{2}", direction, Environment.NewLine, output);

        var rules = new List<FirewallRule>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            var parts = t.Split('|');
            if (parts.Length >= 2)
                rules.Add(new FirewallRule(parts[0].Trim(), parts[1].Trim(), parts.Length >= 3 ? parts[2].Trim() : "Any"));
        }
        return rules;
    }

    // Parses `netsh advfirewall show allprofiles` output.
    // firewallEnabled      = true if any profile has State = ON
    // outboundDefaultBlock = true if any profile has "BlockOutbound" in its Firewall Policy line
    private static void ParseProfileOutput(string output, out bool firewallEnabled, out bool outboundDefaultBlock)
    {
        firewallEnabled      = false;
        outboundDefaultBlock = false;
        foreach (var raw in output.Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith("State", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && tokens[^1].Equals("ON", StringComparison.OrdinalIgnoreCase))
                    firewallEnabled = true;
            }
            else if (t.StartsWith("Firewall Policy", StringComparison.OrdinalIgnoreCase) &&
                     t.Contains("BlockOutbound", StringComparison.OrdinalIgnoreCase))
            {
                outboundDefaultBlock = true;
            }
        }
    }

    // Runs an external command and returns its stdout. Never throws.
    private static async Task<string> RunCommandAsync(string executable, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(executable, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Check: TCP Connectivity ───────────────────────────────────────────────

    private async Task CheckTcpAsync()
    {
        var targets = _resolvedIPs.Count > 0 ? _resolvedIPs : [host];
        foreach (var ip in targets)
        {
            log.Info("Dialing TCP {0}:{1} (timeout 5s)...", ip, port);
            using var tcp = new TcpClient();
            var sw = Stopwatch.StartNew();
            try
            {
                await tcp.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                Pass($"TCP:{ip}", $"{ip}:{port} connected in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var reason = CategorizeTcpError(ex);
                Fail($"TCP:{ip}", $"{ip}:{port} — {reason}");
                log.Debug("Raw error: {0}", ex.Message);
            }
        }
    }

    // ── Check: gRPC (HTTP/2 + application/grpc frame) ─────────────────────────

    private async Task CheckGrpcAsync(bool useProxy)
    {
        var label = useProxy ? "gRPC — default proxy behaviour (mirrors C# SDK)"
                             : "gRPC — proxy bypassed (direct h2c)";
        log.Info("{0}", label);

        var path = $"{TargetUrl}/controller.v1.ControllerAPI/StartSession";
        log.Info("POST {0}", path);
        log.Info("Content-Type: application/grpc | 5-byte empty gRPC frame");
        if (useProxy) log.Info("UseProxy: true  (same as Grpc.Net.Client with no custom HttpHandler)");
        else          log.Info("UseProxy: false (same as Grpc.Net.Client with HttpClientHandler {{ UseProxy = false }})");

        var handler = new HttpClientHandler { UseProxy = useProxy };
        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };

        var grpcFrame = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 };
        using var content = new ByteArrayContent(grpcFrame);
        content.Headers.ContentType = new("application/grpc");

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.TryAddWithoutValidation("TE", "trailers");

        var checkName = useProxy ? "gRPC (default)" : "gRPC (no proxy)";
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();

            string? grpcStatus = null;
            if (response.Headers.TryGetValues("grpc-status", out var hv)) grpcStatus = string.Join(",", hv);
            if (grpcStatus is null && response.TrailingHeaders.TryGetValues("grpc-status", out var tv)) grpcStatus = string.Join(",", tv);
            string? grpcMessage = null;
            if (response.Headers.TryGetValues("grpc-message", out var mv)) grpcMessage = string.Join(",", mv);

            Pass(checkName, $"connected in {sw.ElapsedMilliseconds}ms — grpc-status={grpcStatus ?? "?"} ({GrpcStatusName(grpcStatus)})");
            log.Sub("HTTP status    : {0}", response.StatusCode);
            log.Sub("HTTP version   : {0}", response.Version);
            log.Sub("grpc-status    : {0}  ({1})", grpcStatus ?? "(not in headers)", GrpcStatusName(grpcStatus));
            if (grpcMessage is not null) log.Sub("grpc-message   : {0}", grpcMessage);
            log.Info("Any gRPC response (even an error code) confirms the network path is open.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var category = CategorizeGrpcException(ex);
            Fail(checkName, $"failed in {sw.ElapsedMilliseconds}ms: {category}");
            log.Debug("Full exception: {0}", ex);

            if (IsStreamLevelError(ex))
            {
                log.Info("HTTP/2 stream was reset by server — trying HTTP/1.1 GET to identify the server...");
                await ProbeHttpFallbackAsync(host, port, useProxy);
            }
        }
    }

    // ── Check: AskUI Controller discovery on well-known ports ─────────────────
    //
    // Port 23000 — standalone Controller (launched directly, no Core Service)
    // Port 26000 — managed by the AskUI Core Service
    //
    // The Core Service on port 26000 proxies gRPC to the Controller on 23000,
    // but may require a valid SessionGUID — an empty probe is rejected at the
    // HTTP/2 stream level. TCP success + stream rejection still means the
    // Core Service is reachable.

    private static readonly int[] ControllerPorts = [23000, 26000];

    private async Task CheckAskUIControllerAsync()
    {
        log.Info("Port 23000 = standalone Controller | Port 26000 = AskUI Core Service (proxies to 23000)");
        log.Info("Note: the Core Service may reject an empty probe — TCP open + stream reset still means it is there.");

        var targets = _resolvedIPs.Count > 0 ? _resolvedIPs : [host];

        foreach (var ip in targets)
        {
            foreach (var p in ControllerPorts)
            {
                var addr = $"{ip}:{p}";
                log.Info("Probing {0}...", addr);

                bool tcpOk;
                using (var tcp = new TcpClient())
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await tcp.ConnectAsync(ip, p).WaitAsync(TimeSpan.FromSeconds(5));
                        sw.Stop();
                        tcpOk = true;
                        log.Pass("TCP  {0} — connected in {1}ms", addr, sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        tcpOk = false;
                        log.Fail("TCP  {0} — {1}", addr, CategorizeTcpError(ex));
                    }
                }

                if (!tcpOk)
                {
                    _results.Add(new($"Controller:{p}", false, $"TCP unreachable on port {p}"));
                    continue;
                }

                var url     = $"http://{ip}:{p}";
                var path    = $"{url}/controller.v1.ControllerAPI/StartSession";
                var handler = new HttpClientHandler { UseProxy = false };
                using var client  = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(8) };
                var grpcFrame = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 };
                using var content = new ByteArrayContent(grpcFrame);
                content.Headers.ContentType = new("application/grpc");
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = content,
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                request.Headers.TryAddWithoutValidation("TE", "trailers");

                var gsw = Stopwatch.StartNew();
                try
                {
                    using var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    gsw.Stop();

                    string? grpcStatus = null;
                    if (resp.Headers.TryGetValues("grpc-status", out var hv)) grpcStatus = string.Join(",", hv);
                    if (grpcStatus is null && resp.TrailingHeaders.TryGetValues("grpc-status", out var tv)) grpcStatus = string.Join(",", tv);

                    log.Pass("gRPC {0} — responded in {1}ms, grpc-status={2} ({3})",
                        addr, gsw.ElapsedMilliseconds, grpcStatus ?? "?", GrpcStatusName(grpcStatus));
                    _results.Add(new($"Controller:{p}", true,
                        $"controller responding on port {p} — grpc-status={grpcStatus} ({GrpcStatusName(grpcStatus)})"));
                }
                catch (Exception ex)
                {
                    gsw.Stop();
                    var category = CategorizeGrpcException(ex);

                    if (IsStreamLevelError(ex))
                    {
                        log.Warn("gRPC {0} — probe rejected by server in {1}ms ({2})", addr, gsw.ElapsedMilliseconds, category);
                        if (p == 26000)
                        {
                            log.Sub("Port 26000 is the Core Service — it requires a valid SessionGUID.");
                            log.Sub("TCP is open and the server speaks HTTP/2. The service is REACHABLE.");
                            log.Sub("The empty probe is rejected because SessionGUID is missing — this is expected.");
                        }
                        _results.Add(new($"Controller:{p}", true,
                            $"port {p} open, HTTP/2 stream reset (probe rejected — server is present)"));
                        await ProbeHttpFallbackAsync(ip, p, useProxy: false);
                    }
                    else
                    {
                        log.Fail("gRPC {0} — {1}", addr, category);
                        log.Debug("Full exception: {0}", ex);
                        _results.Add(new($"Controller:{p}", false, $"port {p} open but gRPC failed: {category}"));
                    }
                }
            }
        }
    }

    // ── Summary + Diagnosis ───────────────────────────────────────────────────

    private void PrintSummary()
    {
        // Summary always goes to summaryOut (screen + file in interactive mode).
        var sout = summaryOut;

        sout.WriteLine();
        sout.WriteLine("  " + new string('─', 56));
        sout.WriteLine("    Results Summary");
        sout.WriteLine("  " + new string('─', 56));

        foreach (var r in _results)
        {
            var icon = r.Passed ? "[PASS]" : "[FAIL]";
            sout.WriteLine($"  {icon} {r.Name,-28} {r.Note}");
        }

        var failures = _results.Where(r => !r.Passed).ToList();
        sout.WriteLine();

        if (failures.Count == 0)
        {
            sout.WriteLine($"  All {_results.Count} checks passed. The gRPC connection to {host}:{port} should work.");
            sout.WriteLine();
            return;
        }

        sout.WriteLine($"  {failures.Count} of {_results.Count} check(s) failed.");
        sout.WriteLine();
        sout.WriteLine("  " + new string('─', 56));
        sout.WriteLine("    Diagnosis & Next Steps");
        sout.WriteLine("  " + new string('─', 56));

        bool Has(string prefix, bool passed) =>
            _results.Any(r => r.Name.StartsWith(prefix, StringComparison.Ordinal) && r.Passed == passed);

        if (Has("DNS", false))
        {
            sout.WriteLine("  [WARN] DNS resolution failed — hostname cannot be translated to an IP.");
            sout.WriteLine("         → Run on the TARGET machine:   ipconfig /all");
            sout.WriteLine("           Find the adapter with a real IP (not 169.254.x.x) and use that IP directly.");
            sout.WriteLine("         → Or add the hostname to the hosts file on THIS machine:");
            sout.WriteLine("             Windows : C:\\Windows\\System32\\drivers\\etc\\hosts");
            sout.WriteLine("             Mac/Linux: /etc/hosts");
            sout.WriteLine("             Format  : <ip>  <hostname>");
        }
        else if (Has("Firewall:Outbound", false))
        {
            sout.WriteLine($"  [WARN] Windows Firewall on THIS machine is blocking outbound TCP to port {port}.");
            sout.WriteLine($"         This is the most likely cause of the TCP timeout.");
            sout.WriteLine($"         → Run as administrator on THIS machine:");
            sout.WriteLine($"             netsh advfirewall firewall add rule ^");
            sout.WriteLine($"               name=\"AskUI Client\" dir=out action=allow protocol=TCP remoteport={port}");
            sout.WriteLine($"         → Or check the current outbound default policy:");
            sout.WriteLine($"             netsh advfirewall show allprofiles | findstr \"Policy\"");
        }
        else if (Has("Firewall:Inbound", false) && !Has("TCP", false))
        {
            sout.WriteLine($"  [WARN] Windows Firewall on THIS machine is blocking inbound TCP port {port}.");
            sout.WriteLine($"         This matters only if this machine is the AskUI Controller.");
            sout.WriteLine($"         → Run as administrator on THIS machine:");
            sout.WriteLine($"             netsh advfirewall firewall add rule ^");
            sout.WriteLine($"               name=\"AskUI Controller\" dir=in action=allow protocol=TCP localport={port}");
        }
        else if (Has("TCP", false) && Has("gRPC (no proxy)", false))
        {
            sout.WriteLine($"  [WARN] TCP connection failed — port {port} is not reachable.");
            sout.WriteLine($"         → On the TARGET machine, verify the service is listening:");
            sout.WriteLine($"             netstat -ano | findstr :{port}");
            sout.WriteLine($"           You need 0.0.0.0:{port} — if 127.0.0.1:{port}, it only accepts local connections.");
            sout.WriteLine($"         → Check Windows Firewall inbound rules on the TARGET machine (run as administrator):");
            sout.WriteLine($"             netsh advfirewall firewall add rule ^");
            sout.WriteLine($"               name=\"AskUI Controller\" dir=in action=allow protocol=TCP localport={port}");
            sout.WriteLine($"         → Check for a network-level firewall between the two machines (different subnets/VLANs).");
        }
        else if (Has("TCP", true) && Has("gRPC (no proxy)", false))
        {
            sout.WriteLine($"  [WARN] TCP works but the gRPC probe was rejected on port {port}.");
            sout.WriteLine($"         → If port {port} is the AskUI Core Service: this is expected.");
            sout.WriteLine($"           The Core Service requires a valid SessionGUID in StartSession calls.");
            sout.WriteLine($"           An empty probe frame is rejected at the HTTP/2 stream level — but the");
            sout.WriteLine($"           service IS there. If the Desktop App connects successfully, the service works.");
            sout.WriteLine($"         → If port {port} should be a standalone Controller: verify it is running.");
            sout.WriteLine($"             On the target machine:  netstat -ano | findstr :{port}");
        }
        else if (Has("gRPC (no proxy)", true) && Has("gRPC (default)", false))
        {
            sout.WriteLine("  [WARN] gRPC works when proxy is bypassed but fails with default settings.");
            sout.WriteLine("         → A proxy is intercepting gRPC traffic.");
            sout.WriteLine("         → Fix in the C# SDK — ComputerTargetConnection.cs:");
            sout.WriteLine("             Add:  HttpHandler = new HttpClientHandler { UseProxy = false }");
        }

        sout.WriteLine();
        sout.Flush();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsApipa(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    private static string CategorizeTcpError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        if (msg.Contains("refused",    StringComparison.OrdinalIgnoreCase)) return "connection refused — port reachable but nothing is listening";
        if (msg.Contains("timed out",  StringComparison.OrdinalIgnoreCase)) return "timeout — firewall likely dropping packets silently";
        if (msg.Contains("unreachable",StringComparison.OrdinalIgnoreCase)) return "network unreachable — machines not on same network or routing broken";
        if (msg.Contains("no route",   StringComparison.OrdinalIgnoreCase)) return "no route to host — check network connectivity";
        if (msg.Contains("WSANO_DATA", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("no data of", StringComparison.OrdinalIgnoreCase)) return "DNS WSANO_DATA — hostname known but has no A record; use IP directly";
        return msg;
    }

    private static bool IsStreamLevelError(Exception ex)
    {
        var msg = (ex.InnerException ?? ex).Message;
        return msg.Contains("RST_STREAM",        StringComparison.OrdinalIgnoreCase)
            || msg.Contains("GOAWAY",            StringComparison.OrdinalIgnoreCase)
            || msg.Contains("CANCEL",            StringComparison.OrdinalIgnoreCase)
            || msg.Contains("prematurely",       StringComparison.OrdinalIgnoreCase)
            || msg.Contains("invalid data",      StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unrecognized resp", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("REFUSED_STREAM",    StringComparison.OrdinalIgnoreCase)
            || msg.Contains("INTERNAL_ERROR",    StringComparison.OrdinalIgnoreCase);
    }

    private static string CategorizeGrpcException(Exception ex)
    {
        var msg = (ex.InnerException ?? ex).Message;
        if (IsStreamLevelError(ex))        return $"HTTP/2 stream reset by server ({msg})";
        if (msg.Contains("refused",        StringComparison.OrdinalIgnoreCase)) return "connection refused";
        if (msg.Contains("timed out",      StringComparison.OrdinalIgnoreCase)) return "timeout";
        if (msg.Contains("unreachable",    StringComparison.OrdinalIgnoreCase)) return "network unreachable";
        if (msg.Contains("TLS",            StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("SSL",            StringComparison.OrdinalIgnoreCase)) return $"TLS/SSL error — server may require HTTPS ({msg})";
        return msg;
    }

    private async Task ProbeHttpFallbackAsync(string ip, int p, bool useProxy)
    {
        try
        {
            var handler = new HttpClientHandler { UseProxy = useProxy };
            using var c = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}:{p}/")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var ct = resp.Content.Headers.ContentType?.ToString() ?? "(none)";
            log.Info("HTTP/1.1 fallback GET http://{0}:{1}/ → HTTP {2}, Content-Type: {3}", ip, p, (int)resp.StatusCode, ct);
            if (ct.Contains("grpc", StringComparison.OrdinalIgnoreCase))
                log.Info("Server returned application/grpc content-type — confirms gRPC protocol is present.");
            else
                log.Info("Server does not identify itself as gRPC via HTTP/1.1 — may be a management API.");
        }
        catch (Exception ex2)
        {
            log.Debug("HTTP/1.1 fallback also failed: {0}", ex2.Message);
        }
    }

    private static string GrpcStatusName(string? code) => code switch
    {
        "0"  => "OK",
        "1"  => "CANCELLED",
        "2"  => "UNKNOWN",
        "3"  => "INVALID_ARGUMENT",
        "4"  => "DEADLINE_EXCEEDED",
        "5"  => "NOT_FOUND",
        "6"  => "ALREADY_EXISTS",
        "7"  => "PERMISSION_DENIED",
        "8"  => "RESOURCE_EXHAUSTED",
        "9"  => "FAILED_PRECONDITION",
        "10" => "ABORTED",
        "11" => "OUT_OF_RANGE",
        "12" => "UNIMPLEMENTED",
        "13" => "INTERNAL",
        "14" => "UNAVAILABLE",
        "15" => "DATA_LOSS",
        "16" => "UNAUTHENTICATED",
        null => "no grpc-status in response",
        _    => $"unknown({code})",
    };
}


// ═════════════════════════════════════════════════════════════════════════════
// TeeWriter — writes every character to two TextWriters simultaneously.
// Used so the summary appears on both the screen and in the output file.
// ═════════════════════════════════════════════════════════════════════════════

class TeeWriter(TextWriter primary, TextWriter secondary) : TextWriter
{
    public override Encoding Encoding => primary.Encoding;

    public override void Write(char value)        { primary.Write(value);        secondary.Write(value); }
    public override void Write(string? value)     { primary.Write(value);        secondary.Write(value); }
    public override void WriteLine(string? value) { primary.WriteLine(value);    secondary.WriteLine(value); }
    public override void WriteLine()              { primary.WriteLine();         secondary.WriteLine(); }
    public override void Flush()                  { primary.Flush();             secondary.Flush(); }

    public override async Task FlushAsync()
    {
        await primary.FlushAsync();
        await secondary.FlushAsync();
    }
}
