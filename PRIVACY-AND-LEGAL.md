# Privacy and legal notes

OpenBird is a **personal project**. This page explains what it stores and what leaves your PC, and lists what to check before you use it. **It is not legal advice.** I am not a lawyer. Laws differ by country, state and employer, so ask a qualified person if the stakes are real.

## What OpenBird stores

| Data | Where | Default retention | Encrypted |
| :--- | :--- | :--- | :--- |
| Audio recordings of meetings | `%LOCALAPPDATA%\OpenBird\data\recordings` | Kept until you delete them | No |
| Transcripts, summaries, action items | Database in `%LOCALAPPDATA%\OpenBird\data`, and Markdown in `Documents\OpenBird` | Kept until you delete them | No |
| Screenshots | `%LOCALAPPDATA%\OpenBird\data\screenshots` | **7 days** (`screenshot_retention_days`) | No |
| Text read from the screen (OCR) | Database, and the daily journal in `Documents\OpenBird\timeline` | Kept until you delete it | No |
| Settings and **API keys** | `%LOCALAPPDATA%\OpenBird\config.json` | Until you delete it | No (plain text) |
| Logs | `%LOCALAPPDATA%\OpenBird\logs` | Until you delete them | No |

There is no account, no analytics and no telemetry.

## What leaves your PC

| When | What is sent | To whom |
| :--- | :--- | :--- |
| First run, only if the speech model is missing | A download request (nothing of yours is uploaded) | GitHub (sherpa-onnx release files) |
| AI mode **Cloud**, only after you add a key | Meeting transcripts and text read from your screen, for summaries and Ask | Google (Gemini) or Groq |
| AI mode **On this PC** | Prompts, to a model server **on your own machine** (loopback addresses only) | Nobody else |
| AI mode **Off** | Nothing | Nobody |

In Cloud mode you are sending other people's words, and anything visible on your screen, to a third party that may process it outside your country. Do not use it for confidential or sensitive meetings unless you have checked that this is allowed.

## Why the law applies to you

Recording a meeting and reading your screen both capture **other people's personal data**.

- **GDPR and UK GDPR.** Voices, names, and anything said or shown about a person are personal data. Outside a purely personal or household activity, you generally need a lawful basis, you need to be transparent with the people involved, you must keep the data to what you need, and you must keep it secure. People can ask you to delete it or give them a copy. Whether your use counts as "purely personal" depends on the facts. Recording colleagues, customers or clients usually does not.
- **Recording consent.** Some places allow you to record a conversation you take part in. Others require **every participant** to agree, and recording without that can be a criminal offence. This changes between countries and, in the US, between states. Calls across borders can be covered by more than one law.
- **Employers and contracts.** Work meetings may be covered by a confidentiality agreement, an acceptable-use policy, or a works-council agreement. Running screen capture on a work computer can also break the device policy. Get permission first.
- **Screen capture.** The screen can show passwords, messages, health, financial or other sensitive information about you and about people you are in touch with. OpenBird skips windows that match its exclusion list (password managers, private browsing, sign-in pages, banking), but a title filter cannot catch everything.

## Checklist before you use it

1. Do I have a lawful basis, and do the people I record know? Tell them at the start of the call, and get consent where the law needs it.
2. Is this my own device and my own meeting? If it is for work, do I have written permission?
3. Do I need the recordings at all? If you only need the notes, delete the audio after transcription.
4. Is the PC protected? Turn on disk encryption (BitLocker or Device Encryption) and use a locked user account.
5. Are the OpenBird folders outside any cloud sync that I do not control?
6. Cloud AI: do I accept that transcripts and screen text go to Google or Groq? If not, use **Off** or **On this PC**.
7. Are my exclusion patterns right for the apps I use? Add titles for anything sensitive. The settings key is `capture_exclusions`.
8. Do I know how I will delete data if someone asks, or when the purpose has ended? See below.
9. Is the retention right? Lower `screenshot_retention_days` if you do not need a week of screenshots.

## Deleting your data

Exit OpenBird from its tray icon, then delete what you want gone:

- Everything OpenBird keeps: delete `%LOCALAPPDATA%\OpenBird`.
- Notes and journals: delete `Documents\OpenBird` (or the folder you chose as `storage_dir`).
- One person's data: search the notes and the screen journal for their name, then remove the files or the lines. Remember that the database and the recordings hold the same information.

Uninstalling the program does not delete any of this on purpose, so a mistaken uninstall never costs you your notes.

## Using OpenBird's notes with other tools

The notes are plain files, so other programs can read them, including AI assistants. Anything you give to such a tool is subject to the same rules as above. If an assistant is cloud-based, the content of the notes goes to its provider.

## Security

OpenBird is a personal project and has had no security audit. If you find a security problem, please open a GitHub issue that describes it **without** including real recordings, keys or personal data.

## No warranty

The software is provided as is, under the [PolyForm Noncommercial licence](LICENSE), without warranty of any kind. You are responsible for how you use it.
