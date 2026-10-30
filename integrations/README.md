# Integrations for AI coding agents

## Claude Code skills

Two skills that teach Claude Code to work with desktop UI tests through WinTrace:

| Skill | Use |
|---|---|
| [`debug-ui-test`](claude-code/skills/debug-ui-test/SKILL.md) | Find out why a desktop UI test failed from its trace (Markdown report, screenshot, `wintrace inspect`), then fix it. |
| [`write-ui-test`](claude-code/skills/write-ui-test/SKILL.md) | Write a new desktop test with locators read from the running app (`wintrace snapshot`), following the project's patterns. |

Install them in a test project by copying the folders into its `.claude/skills/`:

```
mkdir -p .claude/skills
cp -r <WinTrace>/integrations/claude-code/skills/* .claude/skills/
```

Adapt them to the project if useful (trace folder, base class, helper names) and commit them, so every session
and every developer gets them.

## Other agents

For Copilot, Cursor, Codex and others, add the snippet from
[docs/ai-agents.md](../docs/ai-agents.md#snippet-for-a-projects-agent-instructions) to the project's
`AGENTS.md`, `.github/copilot-instructions.md` or equivalent.
