# Portable Data Root Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make AM-LINK start and persist data correctly on Windows computers without a D drive.

**Architecture:** Add one focused resolver that evaluates ordered candidate roots with an injectable write probe. App startup and the main window consume the same resolved root instead of hard-coded D paths.

**Tech Stack:** C# 12, .NET 8 WPF, existing console test harness, self-contained win-x64 publish.

## Global Constraints

- Preserve `D:\Codex\ClassInterpreter` when it is available and writable.
- Fall back to the executable directory, then `%LOCALAPPDATA%\AM-LINK`.
- Never store the API Key in files.
- Preserve the 14-day recording policy.

---

### Task 1: Portable root resolver

**Files:**
- Create: `src/ClassInterpreter.Core/Configuration/AppRootResolver.cs`
- Modify: `tests/ClassInterpreter.Tests/Program.cs`

**Interfaces:**
- Produces: `AppRootResolver.Resolve(string executableDirectory, string localAppData, Func<string, bool> isWritable, bool dDriveExists)`.

- [ ] Add failing tests for D, executable-directory, and LocalAppData selection.
- [ ] Run the test harness and verify the new tests fail.
- [ ] Implement ordered candidate selection with normalized absolute paths.
- [ ] Run the tests and verify all resolver tests pass.

### Task 2: Application integration

**Files:**
- Modify: `src/ClassInterpreter.App/App.xaml.cs`
- Modify: `src/ClassInterpreter.App/MainWindow.xaml.cs`
- Modify: `src/ClassInterpreter.App/MainWindow.xaml`
- Modify: `tests/ClassInterpreter.Tests/Program.cs`

**Interfaces:**
- Consumes: `AppRootResolver.ResolveDefault()`.

- [ ] Add failing source-level tests proving both app entry points use the resolver and no hard-coded app root remains.
- [ ] Replace both hard-coded `AppPaths.Create` calls with the resolver.
- [ ] Bind the resolved root to the settings data-path label.
- [ ] Build Release and run the complete test suite.

### Task 3: Portable publication

**Files:**
- Modify: `README.md`
- Generate: `dist/portable-nodrive/`
- Generate: `AM-LINK-Portable-Windows-x64-No-D-Required.zip`

**Interfaces:**
- Produces: a self-contained Windows x64 ZIP containing `AM-LINK/ClassInterpreter.exe` and `使用说明.txt`.

- [ ] Publish self-contained win-x64 output.
- [ ] Launch with a simulated unavailable D drive and verify data creation in the portable root.
- [ ] Package the output without credentials, recordings, or databases.
- [ ] Verify required ZIP entries and compute SHA-256.
