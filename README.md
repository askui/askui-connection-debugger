# AskUI Connection Debugger

A self-contained CLI tool that diagnoses why an AskUI Controller cannot be
reached from a client machine. It walks through every layer — DNS, TCP,
proxy, HTTP/2, and gRPC — and gives a specific fix for whichever layer fails.

No installation required. No .NET runtime required on the target machine.
Copy one binary, run it.

---

## When to use this

Run this on the **machine that is trying to connect** (the one running the
AskUI SDK or desktop app), pointing it at the **machine running the
AskUI Controller**.

```
askui-debug <host>
askui-debug --port 26000 <host>
askui-debug --verbose <host>
```

If you are unsure which port the controller is on, omit `--port`. The tool
defaults to 26000 and also always probes both known controller ports (23000
and 26000) in the dedicated Controller Discovery check.

---

## Download

Grab the binary for your platform from the `bin/` folder:

| Platform | Binary |
|---|---|
| Windows x64 | `askui-debug-windows-x64.exe` |
| Windows ARM64 | `askui-debug-windows-arm64.exe` |
| macOS Apple Silicon | `askui-debug-darwin-arm64` |
| macOS Intel | `askui-debug-darwin-x64` |
| Linux x64 | `askui-debug-linux-x64` |
| Linux ARM64 | `askui-debug-linux-arm64` |

On macOS / Linux, mark the binary executable after downloading:

```bash
chmod +x askui-debug-darwin-arm64
```

---

## Usage

```
askui-debug [flags] <host>

Flags:
  --port, -p <port>   Target port (default: 26000)
  --verbose, -v       Show debug-level output including full stack traces
  --no-color          Disable ANSI colors (useful for log files / CI)
  --help, -h          Show help

Examples:
  askui-debug Winmod-TS4
  askui-debug --port 6769 10.150.122.214
  askui-debug --verbose --no-color 10.150.122.214
```

### About port numbers

The AskUI Controller listens on one of two ports depending on how it was
started:

| Port | When |
|---|---|
| **26000** | Managed by the AskUI Core Service (default after installer setup) |
| **23000** | Standalone — launched directly without the Core Service |

The `--port` flag controls which port the main TCP and gRPC checks target.
The **Controller Discovery** check always probes **both 23000 and 26000**
regardless, so you can use it to find which port the controller is actually
on without guessing.

---

## What it checks

The tool runs seven checks in sequence, then prints a summary and diagnosis.

### 1 — System Information

Prints OS, architecture, hostname, and timestamp. Useful context when sharing
output with the AskUI team.

### 2 — Local Network Interfaces

Lists every non-loopback network adapter on the machine running the tool,
with its status (UP / DOWN) and IPv4 address.

**What to look for:** Any adapter showing a `169.254.x.x` address is tagged
`APIPA`. This means that adapter failed to get a DHCP lease and has no real
network connection. If the only UP adapter shows APIPA, this machine has a
network problem before any connection attempt is made.

### 3 — DNS / Name Resolution

Resolves the hostname using the system resolver — the same code path
(`Dns.GetHostAddressesAsync` → `getaddrinfo`) that the AskUI C# SDK uses.

**What to look for:**
- If resolution fails entirely → try using the raw IP address instead.
- If resolution returns a `169.254.x.x` address → the hostname resolves to an
  adapter that has no DHCP lease. The target machine likely has multiple
  adapters; run `ipconfig /all` on it to find the real IP.
- If resolution returns multiple IPs → the tool tests each one in the TCP
  check.

### 4 — Proxy Configuration

Checks both environment variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`)
and the system proxy (IE / WinHTTP registry settings on Windows) to determine
whether a proxy would intercept traffic to the target.

**Why this matters:** gRPC uses HTTP/2 over plaintext (h2c). Most corporate
HTTP proxies do not support h2c tunnelling, so if a proxy is in the path it
silently drops the connection — giving the same error as "controller not
running". This check tells you whether a proxy is involved before you spend
time on the target machine.

On Windows, the additional tip is to run `netsh winhttp show proxy` to inspect
the WinHTTP proxy separately from the IE settings.

### 5 — TCP Connectivity

Opens a raw TCP connection to each resolved IP on the specified port. This is
the lowest-level reachability test — independent of gRPC or HTTP.

**Error meanings:**

| Error | Meaning |
|---|---|
| Connection refused | Port reachable, but nothing is listening on it |
| Timeout | Firewall is silently dropping packets |
| Network unreachable | Machines not on the same network / routing broken |
| WSANO_DATA | Hostname known to DNS but has no A record — use IP directly |

### 6 — gRPC, proxy bypassed

Sends a minimal gRPC frame (`POST /controller.v1.ControllerAPI/StartSession`,
5-byte empty message body, `Content-Type: application/grpc`) with
`UseProxy = false` on the HTTP handler.

This is equivalent to what the C# SDK does **after** the proxy fix is applied
(`HttpHandler = new HttpClientHandler { UseProxy = false }`).

Any HTTP/2 response — even a gRPC error code — proves the network path is
open end-to-end. The gRPC status codes shown are informational:

| grpc-status | Meaning |
|---|---|
| 0 OK | Controller responded and accepted the request |
| 3 INVALID_ARGUMENT | Controller responded — request was malformed (expected for an empty frame) |
| 14 UNAVAILABLE | Controller is not ready or is restarting |
| no grpc-status | Server did not speak gRPC (wrong port, or not the controller) |

### 7 — gRPC, default proxy behaviour

Same gRPC probe as above, but with `UseProxy = true` (the system default).
This mirrors exactly what the AskUI C# SDK does today (no custom
`HttpHandler` is set in `ComputerTargetConnection.cs`).

**Reading the two gRPC checks together:**

| Check 6 | Check 7 | Diagnosis |
|---|---|---|
| PASS | PASS | No proxy issue — both paths work |
| PASS | FAIL | Proxy is intercepting gRPC traffic → apply the SDK fix |
| FAIL | FAIL | Not a proxy issue — TCP or controller problem |

### 8 — AskUI Controller Discovery (ports 23000 / 26000)

Regardless of the `--port` argument, this check always probes **both 23000
and 26000** with a TCP test followed by a gRPC probe. This helps you:

- Confirm which port the controller is actually listening on.
- Catch the case where the controller is on a different port than you expected.
- Verify the controller is up without having to guess the port.

---

## Reading the summary

At the end, the tool prints a result table and a **Diagnosis** section that
gives the most likely root cause and a copy-pasteable fix command.

Example output when the firewall is blocking port 26000:

```
  [FAIL] TCP:10.150.122.214        timeout — firewall likely dropping packets silently
  [FAIL] gRPC (no proxy)           failed in 5001ms: ...
  ...
  [WARN] TCP connection failed — the port is not reachable.
         → On the TARGET machine, check that the controller is running:
           netstat -ano | findstr :26000
         → Open Windows Firewall on the TARGET machine (run as administrator):
           netsh advfirewall firewall add rule ^
             name="AskUI Controller" dir=in action=allow protocol=TCP localport=26000
```

---

## Building from source

Requires the .NET 10 SDK.

```bash
# All platforms
make all

# Individual targets
make build-windows   # win-x64 + win-arm64
make build-darwin    # osx-arm64 + osx-x64
make build-linux     # linux-x64 + linux-arm64

# Dev run (no build step)
make run ARGS="Winmod-TS4"
make run ARGS="--verbose 10.150.122.214"
```

Binaries land in `bin/` as single self-contained executables (~14–16 MB).
No .NET runtime is required on the machine running the binary.

---

## Sharing output

When reporting a connection issue to the AskUI team, run with `--verbose
--no-color` and paste the full output:

```
askui-debug-windows-x64.exe --verbose --no-color Winmod-TS4 > debug.txt
```
