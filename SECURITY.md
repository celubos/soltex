# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability**. Do not open a public issue, pull request or discussion for a security problem.

Include the affected version or commit, steps to reproduce (with harmless test files only), and the impact you
observed. You will get a reply in the report thread.

## Scope

Examples of what we want to hear about:

- a way to make the elevated helper (`Soltex.exe --system`) do anything outside its fixed catalogue of changes;
- untrusted file names, paths or file contents reaching PowerShell as code, or escaping a folder Soltex writes to;
- quarantine restore or the allow list being abused to overwrite files or trust unintended content;
- Soltex weakening or misreporting the antivirus product registered with Windows;
- the taskbar or audio helper being loaded or controlled by another user or process.

Do not include live malware in reports or pull requests. The standard EICAR test string is fine.

The threat model is in [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).
