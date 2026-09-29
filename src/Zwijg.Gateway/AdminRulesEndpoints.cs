using Zwijg.Core.Audit;
using Zwijg.Core.Security;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record RuleTestInput(ProtectionRule Rule, string Text);

// Anweisungen an die KI, eigene Schutzregeln und Vorlagen für den Chat
public static class AdminRulesEndpoints
{
    private static readonly HashSet<string> Languages = ["de", "en", "auto"];
    private static readonly HashSet<string> Addressings = ["sie", "du", "none"];
    private static readonly HashSet<string> Tones = ["neutral", "friendly", "concise"];
    private static readonly HashSet<string> Lengths = ["short", "normal", "long"];
    private static readonly HashSet<string> Formats = ["auto", "bullets", "prose"];
    private static readonly HashSet<string> Audiences = ["staff", "patients", "none"];

    public static void MapAdminRules(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapPut("/instructions", (InstructionSettings input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            if (Invalid(input) is { } error)
                return Results.BadRequest(new { error });

            return AdminSettingsEndpoints.Change(ctx, store, audit,
                input.Enabled ? "Anweisungen an die KI geändert" : "Anweisungen an die KI ausgeschaltet",
                s => s.Instructions = input);
        });

        // Zeigt, was die KI mit diesen Einstellungen bekommen würde, ohne zu speichern
        admin.MapPost("/instructions/preview", (InstructionSettings input) =>
            Results.Ok(new { text = InstructionComposer.Compose(input) ?? "" }));

        // Die Regeln werden immer als ganze Liste gespeichert, so bleibt die Reihenfolge erhalten
        admin.MapPut("/rules", (List<ProtectionRule> rules, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            foreach (var r in rules)
            {
                r.Name = r.Name.Trim();
                r.Patterns = string.Join("\n", r.Patterns.Replace("\r", "").Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0));
                r.Label = string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim().ToUpperInvariant();
                r.Message = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message.Trim();
            }

            var before = store.Current.ProtectionRules.Select(r => r.Name).ToHashSet();
            var added = rules.Where(r => !before.Contains(r.Name)).Select(r => r.Name).ToList();
            var removed = before.Except(rules.Select(r => r.Name)).ToList();
            var detail = string.Join(", ", added.Select(n => "neu: " + n).Concat(removed.Select(n => "entfernt: " + n)));

            return AdminSettingsEndpoints.Change(ctx, store, audit,
                "Schutzregeln geändert" + (detail.Length > 0 ? " (" + detail + ")" : ""),
                s => s.ProtectionRules = rules);
        });

        // Regel an einem Beispieltext ausprobieren, bevor sie gespeichert wird
        admin.MapPost("/rules/test", (RuleTestInput input) =>
        {
            try
            {
                RuleEngine.Build(input.Rule);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var probe = new ProtectionRule
            {
                Patterns = input.Rule.Patterns, IsRegex = input.Rule.IsRegex,
                CaseSensitive = input.Rule.CaseSensitive, Action = input.Rule.Action, Enabled = true
            };
            var hits = RuleEngine.Find(input.Text ?? "", [probe]);
            return Results.Ok(new { hits = hits.Select(h => new { h.Start, h.Length, h.Value }) });
        });

        admin.MapPut("/templates", (List<PromptTemplate> templates, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            foreach (var t in templates)
            {
                t.Title = t.Title.Trim();
                t.Text = t.Text.Trim();
            }

            return AdminSettingsEndpoints.Change(ctx, store, audit, $"Vorlagen geändert ({templates.Count} Stück)",
                s => s.Templates = templates);
        });
    }

    private static string? Invalid(InstructionSettings i) =>
        !Languages.Contains(i.Language) || !Addressings.Contains(i.Addressing) || !Tones.Contains(i.Tone)
        || !Lengths.Contains(i.Length) || !Formats.Contains(i.Format) || !Audiences.Contains(i.Audience)
            ? "Unbekannte Einstellung beim Antwortstil"
            : null;
}
