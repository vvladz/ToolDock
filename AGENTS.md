# Working on ToolDock

Read [the project context and implementation contract](docs/ai/project-context.md) before changing behavior or architecture. This is context for AI-assisted development; [README.md](README.md) is the user guide.

- Keep all committed documentation, examples, comments, and commit messages in English.
- Write user documentation around configuration and everyday operations. Keep examples inline and avoid release-specific headings or planning language in the guide.
- Keep architectural decisions and implementation constraints in the AI project context. Update it when the user changes a decision.
- Preserve the agreed scope and behavior. Inspect existing code before changing it; prefer minimal changes and existing dependencies and conventions.
- Do not modify unrelated local changes. Review and planning requests do not authorize implementation changes.
- Validate changes at the appropriate level. Report what was checked and any material limitations; do not describe planned behavior as tested or implemented.
- Do not add elaborate failure recovery, high-volume logging machinery, or compatibility with old Windows releases unless requested.
- Require confirmation for irreversible changes. Explicit user authorization for a particular action remains applicable throughout that task.
