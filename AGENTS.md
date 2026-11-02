# Notes for AI agents

Read [`.claude/skills/crystal-fidelity/SKILL.md`](.claude/skills/crystal-fidelity/SKILL.md)
before changing the parser, converter or engine. It sets out how fidelity to Crystal Reports
is established and measured, the format and behaviour already worked out, the conventions
changes follow, and the pitfalls that have cost time.

The short version:

- Describe the `.rpt` format and what Crystal prints. Never decompile or disassemble the
  vendor runtime, and never say anything came from doing so.
- Tests use the public corpora or reports built in code. Private reports never enter the repo
  in any form.
- Run the three test projects separately; a bare `dotnet test` at the root fails (two `.slnx`
  files).
- Every change gets a CHANGELOG entry under `## Unreleased`.
