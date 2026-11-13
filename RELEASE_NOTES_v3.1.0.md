## OpenBird 3.1.0

**A stable release of a personal project.** OpenBird is a local-first meeting recorder and screen memory for Windows 11. It records, transcribes offline, reads your screen with OCR, and writes plain Markdown notes.

> **Check your data-protection and recording laws before you use it.** It records other people and captures text from your screen. Read [PRIVACY-AND-LEGAL.md](https://github.com/TH0MSX/OpenBird/blob/main/PRIVACY-AND-LEGAL.md). Not legal advice.
>
> **Recommended:** clone the repository, read the code and tweak what you need (`git clone https://github.com/TH0MSX/OpenBird.git`, then `cd OpenBird\dotnet` and `.\install.ps1`). The MSI below is a convenience.

### What is in this release

- **A native Windows app.** WinUI 3 on .NET 10 with Home, Meetings, Ask, Chat, Digest and Tasks.
- **Three AI modes.** Off, On this PC (a local model server such as Foundry Local), or Cloud (Gemini or Groq, only with your own key).
- **Chat tab.** A general assistant on the model you choose. It does not read your meetings.
- **Windows notifications and a rebuilt tray menu**, with switches to turn notifications and the Tasks tab off.
- **An MSI installer.** Per user, no administrator rights, with an option to start OpenBird when you sign in.

### Download

| File | Size | SHA-256 |
| :--- | :--- | :--- |
| `{{MSI}}` | {{SIZE}} | `{{SHA256}}` |

Check it in PowerShell with `Get-FileHash .\{{MSI}}`. The installer is **unsigned**, so Windows SmartScreen warns that the publisher is unknown. Choose **More info**, then **Run anyway**, only if the hash matches.

### Known limits

- Tested on one Windows 11 **ARM64** PC. The code builds for x64, but it has **not been run on x64**, and this release has no x64 MSI. On x64, build from source.
- API keys, screenshots and the database are stored **unencrypted**.
- Summaries from small local models can be wrong. Check important facts against the transcript.
- Cloud mode sends transcripts and screen text to Google or Groq.

### Not in this version

Code signing, auto-update, and separating individual remote speakers.

**Licence:** PolyForm Noncommercial 1.0.0. Noncommercial use only.
