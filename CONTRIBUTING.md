# Contributing to PulseSharp

Thank you for your interest! PulseSharp is in early development, so the design still changes often.

## Before you start

- **Bugs and small fixes:** open a pull request.
- **New features or API changes:** open an issue first, so we can agree on the design before you write code.

## Building

You need the .NET SDK version in [`global.json`](global.json).

```bash
dotnet build
dotnet test
```

## Code style

The build enforces the style in [`.editorconfig`](.editorconfig). The main rules are:

- Braces on every `if`, `else`, `for`, `foreach`, `while` and `using`.
- Block bodies for methods and properties, not `=>`.
- Clear names instead of comments. Public APIs get XML documentation.
- Keep it light: no new package dependency without discussing it in an issue first.

## Commits and pull requests

- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `test:`, `build:`, `ci:`, `chore:`.
- One topic per pull request. Pull requests are squash-merged.
- The build and all tests must pass.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
