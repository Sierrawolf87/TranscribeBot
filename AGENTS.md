# AGENTS.md

This file provides guidance to Codex when working with this repository.

## Repository position

TranscribeBot is a .NET Telegram bot that transcribes, normalizes, and summarizes audio with an OpenRouter-backed AI service.

## Build / test / run

| Task | Command |
| --- | --- |
| Build | `dotnet build TranscribeBot.sln` |
| Run locally | `dotnet run --project TranscribeBot/TranscribeBot.csproj` |

## Configuration & secrets

Telegram and OpenRouter credentials are supplied through application configuration. Do not record credential values here.

## Architecture you need to know

- `TelegramBotHostedService` receives Telegram updates, enforces the allowlist, and queues user work.
- `TranscribeService.ProcessAudioAsync` prepares audio, invokes the AI service, records token usage, and formats Telegram-sized results.
- `UserProcessingQueue` limits globally expensive work and can preserve per-user ordering for contextual processing.

## Code conventions

- Snapshot user audio settings before enqueueing work.
- Keep context disabled for isolated processing modes such as guest queries.

## Memory maintenance

Codex may update this file when it learns durable project knowledge that will help future work, especially verified build/test/run commands, project layout, conventions, integration constraints, known local setup issues, and explicit user preferences. Keep updates factual, compact, and free of secrets.

## Decisions

- 2026-07-10: Telegram guest queries only normalize supported media from the replied-to message and answer once through `answerGuestQuery`; they do not use conversation context or status messages.

## Recent Work

- 2026-07-10: `dotnet build TranscribeBot.sln` succeeds; restore reports NU1903 for the transitive `Microsoft.OpenApi` 2.0.0 package.
