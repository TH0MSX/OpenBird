# Changelog

## 3.1.0 - 2026-11-13

First public release. A native Windows 11 app (WinUI 3, .NET 10) that replaces the earlier Python version.

### Added
- Native WinUI 3 app with Home, Meetings, Ask, Chat, Digest and Tasks, and a Settings page.
- Three AI modes: Off, On this PC (any OpenAI-compatible local server, tested with Foundry Local), and Cloud (Gemini or Groq).
- Chat tab for general questions on the model you choose.
- Windows app notifications, with a tray-balloon fallback, and a rebuilt tray menu.
- Settings to turn notifications and the Tasks tab off.
- Per-user MSI installer (WiX 5) with a start-with-Windows option and a desktop shortcut option. No administrator rights.
- New icon and a Fluent-style interface.

### Changed
- Local model calls are more robust: longer timeout, model-name aliases, one retry on malformed JSON, a visible notice when the model does not answer.
- Long transcripts are summarised in chunks so small models can cope.

### Known limits
- API keys, screenshots and the database are not encrypted.
- Tested on one Windows 11 ARM64 PC. No x64 build in the release. Unsigned MSI.

### Not in this version
- Code signing, auto-update, speaker separation for remote participants.
