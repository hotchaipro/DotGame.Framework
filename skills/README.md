# Agent skills

Skills for AI coding agents working in a game that uses DotGame. A skill is a folder with a
`SKILL.md` (instructions, with a `name` and a `description` that says when to use it) and
optional `references/` the agent reads when it needs them. They follow the Agent Skills format.

| Skill | Covers |
|---|---|
| [dotgame-framework](dotgame-framework/SKILL.md) | The game lifecycle and threading rules, loading content and the content items, the platforms and publishing (web, Android, iOS, desktop), and common build errors |

## Installing

Copy the skill's folder to where your agent loads skills from: usually a skills folder in your game's
repository, to use it in that project, or in your home folder, to use it in all of them. Each agent
documents its own folder. An agent that doesn't support skills can still use one: point it at the
`SKILL.md`, for example from your project's `AGENTS.md`, and it reads the references from there.

The agent loads a skill when a task matches its description. Update the copy when you update
DotGame, so it describes the version you use.
