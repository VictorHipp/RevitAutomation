@echo off
rem Prepara este PC para compilar RevitAutomation: instala lo que falte de SDK de .NET 10 y 8, Git y GitHub CLI.
rem Lo que ya esta instalado se salta. Windows pide permiso de administrador una vez. Despues: CREAR_INSTALADOR.cmd.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0preparar_pc.ps1" %*
pause
