@echo off
rem visual-relay.cmd - the Windows sibling of the bash `visual-relay` launcher.
rem A thin shim: it does nothing but hand off to the PowerShell bootstrap next to
rem it (visual-relay.ps1), which provisions the toolchain and execs the C# CLI.
rem Same stem as the Unix launcher, so users type the same `visual-relay <cmd>`
rem on every platform (the gradlew/gradlew.bat, mvnw/mvnw.cmd pattern).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0visual-relay.ps1" %*
