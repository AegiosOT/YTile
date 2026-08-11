using System.Text.Json.Serialization;

namespace YTile.Protocol;

/// <summary>One NDJSON line from CLI to daemon.</summary>
public sealed record CommandRequest(string Cmd, string? Arg = null);

/// <summary>One NDJSON line back. <see cref="State"/> is set only for the state command.</summary>
public sealed record CommandReply(bool Ok, string? Error = null, string? Message = null, StateDto? State = null);

public sealed record RectDto(int X, int Y, int W, int H);

public sealed record WindowDto(long Hwnd, uint Pid, string Exe, string Title, RectDto Rect);

public sealed record WorkspaceDto(string Layout, int Focused, IReadOnlyList<WindowDto> Windows, IReadOnlyList<WindowDto> Floating);

public sealed record MonitorDto(string Device, bool Primary, RectDto WorkArea, int Active, IReadOnlyList<WorkspaceDto> Workspaces);

public sealed record StateDto(string Version, bool Paused, bool DryRun, IReadOnlyList<MonitorDto> Monitors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandReply))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
