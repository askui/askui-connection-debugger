# AskUI Connection Debugger

A self-contained CLI tool that diagnoses why an AskUI Controller cannot be
reached from a client machine. It walks through every layer of the OSI model —
from Layer 2 (ARP / MAC reachability) up through Layer 7 (DNS, proxy, gRPC) —
and gives a specific fix for whichever layer fails.

No installation required. No .NET runtime required on the target machine.
Copy one binary, run it.

---

## When to use this

Run this on the **machine that is trying to connect** (the one running the
AskUI SDK or Desktop App), pointing it at the **machine running the
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

Grab the binary for your platform from the GitHub Releases page:

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
  askui-debug controller-host
  askui-debug --port 26000 192.168.1.100
  askui-debug --verbose --no-color 192.168.1.100
```

### Interactive mode

Double-click the binary (or run it with no arguments) to enter interactive
mode. The tool will prompt for the hostname/IP and an output file path, run
all checks, write the results to a `.txt` file on the Desktop, and display a
summary in the terminal window.

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

The tool runs thirteen checks in sequence, then prints a summary and
diagnosis. Checks are ordered from the lowest OSI layer upward so that each
failure narrows the problem before moving on.

### 1 — System Information

Prints OS, architecture, hostname, and timestamp. Useful context when sharing
output with the AskUI team.

### 2 — Local AskUI Service

Checks whether the AskUI Core Service is listening on port 26000 on **this**
machine (the one running the tool). Expected to pass when the tool is run on
the Controller machine itself; will be informational when run on a client
machine.

### 3 — Local Network Interfaces  _(Layer 1 / 2)_

Lists every non-loopback network adapter with its status (UP / DOWN) and IPv4
address.

**What to look for:** Any adapter showing a `169.254.x.x` address is tagged
`APIPA`. This means that adapter failed to get a DHCP lease and has no real
network connectivity. If the only UP adapter shows APIPA, the problem is on
this machine before any connection is attempted.

### 4 — DNS / Name Resolution  _(Layer 3 / 7)_

Resolves the hostname using the system resolver — the same code path
(`Dns.GetHostAddressesAsync` → `getaddrinfo`) that the AskUI C# SDK uses.

**What to look for:**
- Resolution fails entirely → use the raw IP address instead.
- Resolution returns a `169.254.x.x` address → the target machine has
  multiple adapters; run `ipconfig /all` on it to find the real IP.
- Multiple IPs returned → the tool tests each one in the TCP check.

### 5 — Route / Subnet Analysis  _(Layer 3)_

Determines whether the target IP is on the **same subnet** as a local
interface, or on a **different subnet** that must be reached through a
gateway.

**Why it matters:** A cross-subnet path means a router or inter-VLAN firewall
sits between the two machines. Silent TCP timeouts in this configuration
usually indicate a network-level ACL rather than a Windows Firewall rule on
either endpoint.

### 6 — ARP Cache  _(Layer 2)_

Looks up the target IP in the local ARP table. A hit confirms the host has
been seen at Layer 2 (same broadcast domain) recently.

**What to look for:** An ARP miss is normal when the target is on a different
subnet (traffic goes through the gateway) or on first contact. It is not
recorded as a failure — use it together with the Route check to understand
the path.

### 7 — ICMP Ping  _(Layer 3)_

Sends four ICMP echo requests and reports round-trip time and TTL.

**Cross-correlate with TCP:**

| ICMP | TCP | Interpretation |
|---|---|---|
| pass | pass | Network is fully open |
| pass | fail | Host is alive — block is at Layer 4 (port / firewall) |
| fail | fail | Host unreachable at network level, or ICMP blocked by firewall |
| fail | pass | ICMP blocked by firewall (common), TCP works fine |

Note: Windows Firewall blocks ICMP by default. An ICMP failure alone does
not indicate a network problem.

### 8 — Proxy Configuration  _(Layer 7)_

Checks both environment variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`)
and the system proxy (IE / WinHTTP registry settings on Windows).

**Why this matters:** gRPC uses HTTP/2 over plaintext (h2c). Most corporate
HTTP proxies do not support h2c tunnelling — a proxy in the path silently
drops the connection, giving the same symptom as "controller not running."

### 9 — Windows Firewall Rules  _(Layer 4)_

Reads the global Windows Firewall policy (on/off, default inbound/outbound
actions) and queries enabled rules for the target port.

- **Inbound rules** — relevant when this machine is the AskUI Controller.
- **Outbound rules** — relevant when this is the client connecting remotely.
  An outbound block explains a TCP timeout without any visible error.

On macOS / Linux the equivalent manual commands are printed instead.

### 10 — TCP Connectivity  _(Layer 4)_

Opens a raw TCP connection to each resolved IP on the target port. This is
the lowest-level reachability test — independent of gRPC or HTTP.

**Error meanings:**

| Error | Meaning |
|---|---|
| Connection refused | Port reachable, nothing listening |
| Timeout | Firewall silently dropping packets |
| Network unreachable | Routing broken between the two machines |
| WSANO_DATA | Hostname resolves in DNS but has no A record — use IP |

### 11 — gRPC, proxy bypassed  _(Layer 7)_

Sends a minimal gRPC frame with `UseProxy = false`. This mirrors what the
AskUI C# SDK does after the proxy fix is applied
(`HttpHandler = new HttpClientHandler { UseProxy = false }`).

Any HTTP/2 response — even a gRPC error code — proves the full network path
is open end-to-end.

### 12 — gRPC, default proxy behaviour  _(Layer 7)_

Same gRPC probe, but with `UseProxy = true` (the system default, no custom
`HttpHandler`).

**Reading checks 11 and 12 together:**

| Check 11 | Check 12 | Diagnosis |
|---|---|---|
| PASS | PASS | No proxy issue |
| PASS | FAIL | Proxy intercepts gRPC → apply the SDK fix |
| FAIL | FAIL | Not a proxy issue — TCP or controller problem |

### 13 — AskUI Controller Discovery  _(Layer 7)_

Probes **both port 23000 and port 26000** with TCP + gRPC, regardless of the
`--port` argument. Helps you confirm which port the controller is on and
whether it is responding.

---

## Reading the summary

At the end, the tool prints a result table and a **Diagnosis** section that
gives the most likely root cause and a copy-pasteable fix command.

Example output when a firewall is blocking port 26000:

```
  [FAIL] TCP:192.168.1.100         timeout — firewall likely dropping packets silently
  [FAIL] gRPC (no proxy)           failed in 5001ms: ...
  ...
  [WARN] TCP connection to port 26000 failed.
         ICMP ping succeeded — the host is alive at the network level.
         The block is at Layer 4 (TCP/port level), not the network.
         → Check Windows Firewall inbound rules on the TARGET machine ...
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
make run ARGS="controller-host"
make run ARGS="--verbose 192.168.1.100"
```

Binaries land in `bin/` as single self-contained executables (~14–16 MB).
No .NET runtime is required on the machine running the binary.

---

## Sharing output

When reporting a connection issue to the AskUI team, run with `--verbose
--no-color` and share the output file:

```
askui-debug-windows-x64.exe --verbose --no-color controller-host > debug.txt
```

Or use interactive mode — double-click the binary, enter the hostname when
prompted, and send the `.txt` file written to your Desktop.
