# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project
- **Type**: C# console application (.NET 8.0)
- **Purpose**: Translation service integrating with Kimi API for game text translation (BG3, XCOM)
- **Entry point**: `Program.cs`

## Build Commands
- Build: `dotnet build`
- Run: `dotnet run`
- Publish: `dotnet publish -c Release`

## Branch Conventions
- Prefix with type: `feature/`, `bugfix/`, `hotfix/`

## Notes
- No test project exists; verify changes manually via `dotnet run`
- `TranslationApiService.cs` handles Kimi API communication
- `BG3Translater.cs` and `XCOMTranslater.cs` are game-specific translation modules
