@echo off
rem Crea Instalar_RevitAutomation.exe (pruebas + compilacion + empaquetado). No instala nada en este equipo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0crear_instalador.ps1" %*
pause
