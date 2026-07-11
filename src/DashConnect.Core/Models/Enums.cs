namespace DashConnect.Core.Models;

/// <summary>Per-service reachability classification derived from probing.</summary>
public enum ServiceVerdict
{
    Unknown,
    Open,         // TLS/WebSocket handshake succeeded with acceptable latency
    Throttled,    // handshake succeeded but latency is abnormally high
    Blocked,      // TCP connects but TLS/handshake is reset or times out (classic DPI)
    Unreachable   // TCP connect itself fails (no route / offline)
}

/// <summary>High-level lifecycle state of an engine (Zapret or Sing-box).</summary>
public enum EngineState
{
    Stopped,
    Starting,
    Testing,
    Running,
    Error
}
