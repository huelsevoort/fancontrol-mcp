using FanControl.MCP.Mcp;

namespace FanControl.MCP;

/// <summary>
/// The plugin's built-in "skill": FanControl tuning best practices, delivered three ways so every MCP client gets it:
/// as resource (fancontrol://guides/fan-curve-best-practices), as prompt templates (slash commands in Claude Code /
/// Claude Desktop) with the guide embedded, and through the get_fan_curve_guide tool for clients that only use tools.
/// </summary>
internal static class TuningSkill
{
    public const string GuideUri = "fancontrol://guides/fan-curve-best-practices";

    private static readonly Lazy<string> GuideText = new(() => LoadEmbedded("fan-curve-best-practices.md"));

    public static string Guide => GuideText.Value;

    public static IReadOnlyList<McpResource> Resources { get; } =
    [
        new McpResource(GuideUri, "fan-curve-best-practices", "FanControl fan curve best practices",
            "How to choose temperature sources, shape fan curves, set hysteresis/response time, step up/down, start/stop, " +
            "safety limits per component and a step-by-step tuning workflow.",
            "text/markdown", () => Guide),
    ];

    public static IReadOnlyList<McpPrompt> Prompts { get; } =
    [
        new McpPrompt("setup_fan_control", "Set up FanControl (guided)",
            "Guided setup: the assistant interviews you about goal, room, case, fan layout, coolers and GPU, proposes a baseline configuration and validates it under load.",
            [
                new McpPromptArgument("goal", "Optional: quiet_no_perf_loss, balanced, max_performance, silent or constant"),
            ],
            args =>
            {
                var goal = args.TryGetValue("goal", out var g) ? $" My goal: {g}." : "";
                return $"""
                    Please set up FanControl with me from scratch using the guided setup wizard.{goal}
                    1. Call setup_wizard (if your client shows forms, prefer setup_wizard_form) and walk me through the open steps one at a time.
                       Confirm detected hardware instead of asking for it, explain briefly why a question matters, accept "don't know".
                    2. Save my answers with save_system_profile after each step.
                    3. When complete, call propose_baseline_config, explain the plan and its warnings in plain words, and wait for my OK.
                    4. Apply it with apply_baseline_config, then help me validate it under my real workload with start_measurement / mark_phase /
                       get_measurement_summary (guard on). Compare clocks and temperatures; tune if needed.
                    5. Only call save_config when I explicitly agree.
                    Follow the attached best-practice guide. Answer in the language I use.
                    """;
            },
            [GuideUri]),

        new McpPrompt("tune_fan_curves", "Tune fan curves",
            "Review the current FanControl setup and tune fan curves following the built-in best practices.",
            [
                new McpPromptArgument("goal", "silent, balanced or performance (default: balanced)"),
                new McpPromptArgument("focus", "Optional: what to tune, e.g. 'CPU', 'case fans', 'AIO', 'GPU', or a control name"),
                new McpPromptArgument("notes", "Optional: anything else (room temperature, hardware, what bothers you)"),
            ],
            args =>
            {
                var goal = args.GetValueOrDefault("goal", "balanced");
                var focus = args.GetValueOrDefault("focus", "all fans");
                var notes = args.TryGetValue("notes", out var n) ? $"\nAdditional notes from me: {n}" : "";
                return $"""
                    Please tune my FanControl fan curves. Goal: {goal}. Focus: {focus}.{notes}

                    Follow the attached best-practice guide exactly:
                    1. Read the current state with get_snapshot, list_fan_curves and analyze_fan_setup.
                    2. Summarise what you found (temperature sources, curve shapes, problems) in a short table.
                    3. Propose concrete changes (points, sources, hysteresis/response time, step up/down) with a one-line reason each.
                    4. Wait for my confirmation before changing anything.
                    5. After I agree, apply the changes, re-check the snapshot and tell me how to verify under load.
                    6. Only call save_config when I explicitly say so.
                    Answer in the language I use.
                    """;
            },
            [GuideUri]),

        new McpPrompt("diagnose_fan_noise", "Diagnose fan noise",
            "Find the cause of annoying fan behaviour (ramping, humming, clicking, loud at idle, hot under load).",
            [
                new McpPromptArgument("symptom", "What you hear or see, e.g. 'fans ramp up and down every few seconds'", Required: true),
            ],
            args => $"""
                My fans behave like this: {args["symptom"]}

                Use the attached guide (especially the diagnosis table) to find the cause:
                read get_snapshot, list_controls, list_fan_curves and analyze_fan_setup, take a second snapshot after ~15 s to see
                how values move, then explain the most likely cause and propose a fix. Do not change anything until I confirm.
                Answer in the language I use.
                """,
            [GuideUri]),

        new McpPrompt("review_fan_setup", "Review fan setup (read-only)",
            "Read-only health check of the FanControl configuration against the best practices. Makes no changes.",
            [],
            _ => """
                Do a read-only review of my FanControl setup against the attached best practices.
                Use get_snapshot, list_fan_curves and analyze_fan_setup. Report: safety issues first, then noise/stability
                improvements, then nice-to-haves, each with the concrete setting you would change. Do not call any tool that
                changes fans or configuration. Answer in the language I use.
                """,
            [GuideUri]),
    ];

    private static string LoadEmbedded(string fileName)
    {
        var asm = typeof(TuningSkill).Assembly;
        using var stream = asm.GetManifestResourceStream("FanControl.MCP.Guides." + fileName)
                           ?? throw new InvalidOperationException($"Embedded guide '{fileName}' missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
